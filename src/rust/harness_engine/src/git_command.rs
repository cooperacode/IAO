//! Runner pequeno e shell-safe para comandos Git. A engine fornece o mecanismo; flows
//! decidem quais comandos rodar e como interpretar o resultado.

use std::path::{Path, PathBuf};
use std::process::Command;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GitCommandResult {
    pub exit_code: i32,
    pub output: String,
    pub error: String,
}

/// Diretório estável e sempre vazio usado como `core.hooksPath` em todo comando git
/// disparado pelo harness: neutraliza hooks do repositório-alvo (pre-commit/post-commit
/// etc.), que de outra forma rodariam código arbitrário controlado pelo próprio agente
/// supervisionado (RFC §6.11).
fn no_hooks_dir() -> PathBuf {
    let dir = std::env::temp_dir().join("iao-no-hooks");
    let _ = std::fs::create_dir_all(&dir);
    dir
}

pub fn run(working_directory: impl AsRef<Path>, args: &[&str]) -> GitCommandResult {
    let hooks_path = no_hooks_dir();
    let hooks_path_arg = format!("core.hooksPath={}", hooks_path.display());

    // Isolamento de Git (RFC §6.11): à frente dos args do chamador, sempre. Neutraliza
    // hooks (core.hooksPath para um diretório vazio), credential helper (evita prompt ou
    // vazamento de credencial armazenada) e pager (core.pager=cat evita travar num
    // subprocesso interativo esperando stdin que nunca chega).
    let output = Command::new("git")
        .args(["-c", &hooks_path_arg])
        .args(["-c", "credential.helper="])
        .args(["-c", "core.pager=cat"])
        .args(args)
        .current_dir(working_directory)
        .output();

    match output {
        Ok(out) => GitCommandResult {
            exit_code: out.status.code().unwrap_or(-1),
            output: String::from_utf8_lossy(&out.stdout).to_string(),
            error: String::from_utf8_lossy(&out.stderr).to_string(),
        },
        Err(e) => GitCommandResult {
            exit_code: -1,
            output: String::new(),
            error: e.to_string(),
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn run_comando_valido_captura_stdout() {
        let dir = tempfile::tempdir().unwrap();

        let result = run(dir.path(), &["--version"]);

        assert_eq!(result.exit_code, 0);
        assert!(result.output.contains("git version"));
    }

    #[test]
    fn run_diretorio_inexistente_retorna_erro_sem_lancar() {
        let dir = tempfile::tempdir().unwrap();
        let missing = dir.path().join("missing");

        let result = run(&missing, &["status"]);

        assert_eq!(result.exit_code, -1);
        assert!(!result.error.is_empty());
    }

    #[test]
    fn run_injeta_isolamento_de_hooks_e_pager() {
        // `git config --get` enxerga overrides de `-c` na pilha de config, então dá para
        // confirmar que run() sempre os injeta sem precisar de um repositório real.
        let dir = tempfile::tempdir().unwrap();

        let hooks_path = run(dir.path(), &["config", "--get", "core.hooksPath"]);
        assert_eq!(hooks_path.exit_code, 0);
        assert!(hooks_path.output.trim().ends_with("iao-no-hooks"));

        let pager = run(dir.path(), &["config", "--get", "core.pager"]);
        assert_eq!(pager.exit_code, 0);
        assert_eq!(pager.output.trim(), "cat");
    }
}
