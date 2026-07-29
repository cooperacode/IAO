//! Small, shell-safe runner for Git commands. The engine provides the mechanism; flows
//! decide which commands to run and how to interpret the result.

use std::path::{Path, PathBuf};
use std::process::Command;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GitCommandResult {
    pub exit_code: i32,
    pub output: String,
    pub error: String,
}

/// Stable, always-empty directory used as `core.hooksPath` for every git command fired by
/// the harness: neutralizes the target repository's hooks (pre-commit/post-commit etc.),
/// which would otherwise run arbitrary code controlled by the supervised agent itself
/// (RFC §6.11).
fn no_hooks_dir() -> PathBuf {
    let dir = std::env::temp_dir().join("iao-no-hooks");
    let _ = std::fs::create_dir_all(&dir);
    dir
}

pub fn run(working_directory: impl AsRef<Path>, args: &[&str]) -> GitCommandResult {
    let hooks_path = no_hooks_dir();
    let hooks_path_arg = format!("core.hooksPath={}", hooks_path.display());

    // Git isolation (RFC §6.11): ahead of the caller's args, always. Neutralizes hooks
    // (core.hooksPath pointing to an empty directory), the credential helper (avoids a
    // prompt or leaking a stored credential), and the pager (core.pager=cat avoids
    // hanging on an interactive subprocess waiting for stdin that never arrives).
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
        // `git config --get` sees `-c` overrides in the config stack, so we can confirm
        // that run() always injects them without needing a real repository.
        let dir = tempfile::tempdir().unwrap();

        let hooks_path = run(dir.path(), &["config", "--get", "core.hooksPath"]);
        assert_eq!(hooks_path.exit_code, 0);
        assert!(hooks_path.output.trim().ends_with("iao-no-hooks"));

        let pager = run(dir.path(), &["config", "--get", "core.pager"]);
        assert_eq!(pager.exit_code, 0);
        assert_eq!(pager.output.trim(), "cat");
    }
}
