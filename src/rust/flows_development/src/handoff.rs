//! Handoff automático: commit git do diretório-alvo (excluindo `.harness/`) + linha em
//! `progress.txt`, sem gastar um turno do modelo. Falha em qualquer etapa cai no prompt
//! legado de reparo manual (`prompts::handoff_prompt`).

use std::path::{Path, PathBuf};

use harness_engine::{feature_store, git_command, run_config_store, state_store};

use crate::prompts;

fn state(key: &str) -> String {
    state_store::get(key).unwrap_or_default()
}

pub fn complete_verified_feature(verify_result: &str) -> String {
    match try_automated_handoff(verify_result) {
        Ok(confirmation) => {
            eprintln!("[dev] handoff automatico concluido: {confirmation}");
            if let Ok(id) = state("current_feature_id").parse::<i32>() {
                feature_store::mark_passed(id);
            }
            if feature_store::all_passing() {
                crate::tasks::done()
            } else {
                prompts::bearings_prompt()
            }
        }
        Err(failure) => {
            eprintln!("[dev] handoff automatico falhou: {failure}");
            prompts::handoff_prompt(Some(&failure))
        }
    }
}

fn try_automated_handoff(verify_result: &str) -> Result<String, String> {
    let feature_id: i32 = state("current_feature_id")
        .parse()
        .map_err(|_| "feature atual ausente no state.json".to_string())?;

    let feature = feature_store::load()
        .into_iter()
        .find(|f| f.id == feature_id);
    let mut title = feature
        .map(|f| f.title)
        .unwrap_or_else(|| state("current_feature_title"));
    if title.trim().is_empty() {
        title = format!("feature #{feature_id}");
    }

    let config = run_config_store::load();
    let target_dir = resolve_target_dir(&config.target_dir)?;

    std::fs::create_dir_all(&target_dir)
        .map_err(|e| format!("falha ao atualizar progress.txt: {e}"))?;
    append_progress(
        &target_dir,
        feature_id,
        &title,
        &config.verify_cmd,
        verify_result,
    )
    .map_err(|e| format!("falha ao atualizar progress.txt: {e}"))?;

    let rev_parse = git_command::run(&target_dir, &["rev-parse", "--show-toplevel"]);
    if rev_parse.exit_code != 0 {
        return Ok(format!(
            "NO_GIT: {}",
            one_line(
                &rev_parse.error,
                "diretorio-alvo fora de um repositorio Git"
            )
        ));
    }

    let add = git_command::run(&target_dir, &["add", "-A", "--", ".", ":(exclude).harness"]);
    if add.exit_code != 0 {
        return Err(format!(
            "git add falhou: {}",
            one_line(&add.error, &add.output)
        ));
    }

    let diff = git_command::run(
        &target_dir,
        &[
            "diff",
            "--cached",
            "--quiet",
            "--",
            ".",
            ":(exclude).harness",
        ],
    );
    if diff.exit_code == 0 {
        let head = git_command::run(&target_dir, &["rev-parse", "--short", "HEAD"]);
        return Ok(if head.exit_code == 0 {
            one_line(&head.output, "NO_CHANGES")
        } else {
            "NO_CHANGES".to_string()
        });
    }
    if diff.exit_code > 1 {
        return Err(format!(
            "git diff --cached falhou: {}",
            one_line(&diff.error, &diff.output)
        ));
    }

    let commit = git_command::run(
        &target_dir,
        &[
            "commit",
            "-m",
            &commit_message(feature_id, &title),
            "--",
            ".",
            ":(exclude).harness",
        ],
    );
    if commit.exit_code != 0 {
        return Err(format!(
            "git commit falhou: {}",
            one_line(&commit.error, &commit.output)
        ));
    }

    let status = git_command::run(
        &target_dir,
        &["status", "--short", "--", ".", ":(exclude).harness"],
    );
    if status.exit_code != 0 {
        return Err(format!(
            "git status falhou: {}",
            one_line(&status.error, &status.output)
        ));
    }
    if !status.output.trim().is_empty() {
        return Err(format!(
            "diretorio-alvo ainda sujo apos commit: {}",
            one_line(&status.output, "")
        ));
    }

    let hash = git_command::run(&target_dir, &["rev-parse", "--short", "HEAD"]);
    if hash.exit_code == 0 {
        Ok(one_line(&hash.output, "COMMIT_CREATED"))
    } else {
        Err(format!(
            "commit criado, mas hash nao foi lido: {}",
            one_line(&hash.error, &hash.output)
        ))
    }
}

/// Resolve o diretório-alvo do handoff e rejeita as configurações claramente perigosas ou
/// sem sentido antes de rodar `git add`/`git commit` nele. Containment completo contra uma
/// raiz de política assinada (capability broker) é trabalho de fase futura — isto é só a
/// lista mínima de rejeição do RFC §6.3: vazio, raiz do filesystem, HOME do usuário, ou o
/// diretório de instalação do próprio harness.
pub fn resolve_target_dir(target_dir: &str) -> Result<PathBuf, String> {
    let configured = target_dir.trim();
    if configured.is_empty() {
        return Err("target_dir vazio: nenhum diretório-alvo configurado".to_string());
    }

    let cwd = std::env::current_dir().unwrap_or_default();
    let joined = cwd.join(configured);
    let resolved = joined.canonicalize().unwrap_or(joined);

    if resolved.parent().is_none() {
        return Err(format!(
            "target_dir resolvido para a raiz do sistema de arquivos: {}",
            resolved.display()
        ));
    }

    if let Ok(home) = std::env::var("HOME").or_else(|_| std::env::var("USERPROFILE")) {
        if !home.trim().is_empty() {
            let home_path = PathBuf::from(&home);
            let home_canonical = home_path.canonicalize().unwrap_or(home_path);
            if resolved == home_canonical {
                return Err(format!(
                    "target_dir resolvido para o diretório home do usuário: {}",
                    resolved.display()
                ));
            }
        }
    }

    if let Ok(exe) = std::env::current_exe() {
        if let Some(exe_dir) = exe.parent() {
            let exe_dir_canonical = exe_dir
                .canonicalize()
                .unwrap_or_else(|_| exe_dir.to_path_buf());
            if resolved == exe_dir_canonical {
                return Err(format!(
                    "target_dir resolvido para o diretório de instalação do harness: {}",
                    resolved.display()
                ));
            }
        }
    }

    Ok(resolved)
}

fn append_progress(
    target_dir: &Path,
    feature_id: i32,
    title: &str,
    verify_cmd: &str,
    verify_result: &str,
) -> std::io::Result<()> {
    let summary = one_line(&state("current_feature_summary"), "implementacao concluida");
    let verify = one_line(verify_result, "PASS");
    let command = if verify_cmd.trim().is_empty() {
        "comando de verificacao do projeto".to_string()
    } else {
        verify_cmd.to_string()
    };
    let line = format!(
        "[{} UTC] Feature #{feature_id} - {}: {summary}. Verificar com: {}. Resultado: {verify}",
        chrono::Utc::now().format("%Y-%m-%d %H:%M"),
        one_line(title, ""),
        one_line(&command, "")
    );

    use std::io::Write;
    let mut file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(target_dir.join("progress.txt"))?;
    writeln!(file, "{line}")
}

fn commit_message(feature_id: i32, title: &str) -> String {
    let mut suffix = one_line(title, "");
    if suffix.len() > 72 {
        suffix = truncate_utf8_bytes(&suffix, 72).trim_end().to_string();
    }
    format!("feat(development): complete feature #{feature_id} - {suffix}")
}

/// Corta `text` em no máximo `max_bytes` octetos UTF-8, recuando até a fronteira de char
/// válida mais próxima — nunca parte um caractere multibyte (acento, emoji) ao meio.
/// Compartilhado por `commit_message` e `verify::snippet` (RFC Apêndice B item 1: medir em
/// bytes, não em codepoints, para igualar a semântica entre engines .NET/Python/Rust).
pub fn truncate_utf8_bytes(text: &str, max_bytes: usize) -> String {
    if text.len() <= max_bytes {
        return text.to_string();
    }

    let mut cut = max_bytes;
    while cut > 0 && !text.is_char_boundary(cut) {
        cut -= 1;
    }

    text[..cut].to_string()
}

pub fn one_line(value: &str, fallback: &str) -> String {
    let normalized = value
        .replace('\r', " ")
        .replace('\n', " ")
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");
    if normalized.trim().is_empty() {
        fallback.to_string()
    } else {
        normalized.trim().to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn truncate_utf8_bytes_nao_quebra_caractere_multibyte_no_meio() {
        // "café ☕" — "é" (2 bytes) e "☕" (3 bytes) são multibyte; um corte ingênuo em
        // qualquer posição produziria uma string UTF-8 inválida (panic ao fatiar).
        let text = "café ☕";
        for max in 0..=text.len() {
            let truncated = truncate_utf8_bytes(text, max);
            assert!(truncated.len() <= max);
        }
    }

    #[test]
    fn commit_message_trunca_titulo_longo_em_bytes_utf8() {
        let title = "café ".repeat(20); // bem acima de 72 bytes, com acento perto do corte
        let message = commit_message(1, &title);

        assert!(message.starts_with("feat(development): complete feature #1 - "));
        // Se o corte tivesse partido um "é" ao meio, a formatação acima já teria
        // panicado ao montar a String — chegar aqui já prova o corte válido.
    }
}
