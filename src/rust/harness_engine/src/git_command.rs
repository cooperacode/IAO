//! Runner pequeno e shell-safe para comandos Git. A engine fornece o mecanismo; flows
//! decidem quais comandos rodar e como interpretar o resultado.

use std::path::Path;
use std::process::Command;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GitCommandResult {
    pub exit_code: i32,
    pub output: String,
    pub error: String,
}

pub fn run(working_directory: impl AsRef<Path>, args: &[&str]) -> GitCommandResult {
    let output = Command::new("git")
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
}
