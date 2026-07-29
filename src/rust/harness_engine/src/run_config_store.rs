//! Persists `verify_cmd`/`target_dir` (captured once by `plan`) in
//! `.harness/run_config.json` — deliberately outside `state.json`. `task_registry`
//! unconditionally resets `state.json` on every `start`, before any domain code runs; a
//! resumed run still needs these two values for `smoke`/`verify` to work, so they must
//! survive that reset.

use serde::{Deserialize, Serialize};

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/run_config.json";

/// Verify command, target directory, and run identity (RFC §6.4), all captured once by
/// `plan`. `run_id` is generated only on a genuinely new run — the same moment `write()`
/// is called after `reset()` — and survives every resume because this file isn't touched
/// when `start` decides there's pending work (see the module comment).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RunConfig {
    #[serde(rename = "verifyCmd", default)]
    pub verify_cmd: String,
    #[serde(rename = "targetDir", default = "default_target_dir")]
    pub target_dir: String,
    #[serde(rename = "runId", default)]
    pub run_id: String,
}

fn default_target_dir() -> String {
    ".".to_string()
}

impl Default for RunConfig {
    fn default() -> Self {
        Self {
            verify_cmd: String::new(),
            target_dir: default_target_dir(),
            run_id: String::new(),
        }
    }
}

/// Writes the run configuration — same lifecycle as `feature_list.json` (written by
/// `plan`, deleted only when `start` decides there's no run to resume).
pub fn write(config: &RunConfig) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[RunConfigStore] failed to write: {e}");
        return;
    }
    match serde_json::to_string(config) {
        Ok(json) => {
            if let Err(e) =
                crate::atomic_io::write_atomic(std::path::Path::new(FILE_PATH), &json)
            {
                eprintln!("[RunConfigStore] failed to write: {e}");
            }
        }
        Err(e) => eprintln!("[RunConfigStore] failed to write: {e}"),
    }
}

/// Reads the persisted configuration, or the defaults if nothing has been written yet.
pub fn load() -> RunConfig {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        let loaded = std::fs::read_to_string(p)
            .map_err(|e| e.to_string())
            .and_then(|json| serde_json::from_str::<RunConfig>(&json).map_err(|e| e.to_string()));

        match loaded {
            Ok(config) => return config,
            Err(e) => eprintln!("[RunConfigStore] failed to load: {e}"),
        }
    }
    RunConfig::default()
}

/// Deletes on a genuinely new run — paired with `feature_store::reset`.
pub fn reset() {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        if let Err(e) = std::fs::remove_file(p) {
            eprintln!("[RunConfigStore] failed to clear: {e}");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::lock_cwd;

    struct Isolated {
        _dir: tempfile::TempDir,
        previous: std::path::PathBuf,
    }

    impl Isolated {
        fn new() -> Self {
            let dir = tempfile::tempdir().unwrap();
            let previous = std::env::current_dir().unwrap();
            std::env::set_current_dir(dir.path()).unwrap();
            Self {
                _dir: dir,
                previous,
            }
        }
    }

    impl Drop for Isolated {
        fn drop(&mut self) {
            let _ = std::env::set_current_dir(&self.previous);
        }
    }

    #[test]
    fn write_e_load_fazem_roundtrip() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&RunConfig {
            verify_cmd: "npm test".to_string(),
            target_dir: "app".to_string(),
            run_id: String::new(),
        });

        let loaded = load();

        assert_eq!(loaded.verify_cmd, "npm test");
        assert_eq!(loaded.target_dir, "app");
    }

    #[test]
    fn write_e_load_preservam_o_run_id() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&RunConfig {
            verify_cmd: "npm test".to_string(),
            target_dir: "app".to_string(),
            run_id: "019b1ed0-6bea-7bc1-a790-0bdb42bb8ab6".to_string(),
        });

        let loaded = load();

        assert_eq!(loaded.run_id, "019b1ed0-6bea-7bc1-a790-0bdb42bb8ab6");
    }

    #[test]
    fn load_arquivo_ausente_retorna_defaults_sem_lancar() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let loaded = load();

        assert_eq!(loaded.verify_cmd, "");
        assert_eq!(loaded.target_dir, ".");
    }

    #[test]
    fn reset_apaga_o_arquivo() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write(&RunConfig {
            verify_cmd: "npm test".to_string(),
            target_dir: "app".to_string(),
            run_id: String::new(),
        });

        reset();

        assert_eq!(load(), RunConfig::default());
    }

    #[test]
    fn reset_sem_arquivo_nao_panica() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        reset();
    }
}
