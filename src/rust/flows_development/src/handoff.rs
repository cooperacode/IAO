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
    let target_dir = resolve_target_dir(&config.target_dir);

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

pub fn resolve_target_dir(target_dir: &str) -> PathBuf {
    let configured = if target_dir.trim().is_empty() {
        "."
    } else {
        target_dir
    };
    std::env::current_dir().unwrap_or_default().join(configured)
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
    if suffix.chars().count() > 72 {
        suffix = suffix
            .chars()
            .take(72)
            .collect::<String>()
            .trim_end()
            .to_string();
    }
    format!("feat(development): complete feature #{feature_id} - {suffix}")
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
