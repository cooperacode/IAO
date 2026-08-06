//! Loads golden set cases from disk.

use serde::{Deserialize, Serialize};

use crate::harness_log;

/// A golden set case: the expectation the recorded evidence is measured against.
/// `expect_pass = false` marks an INTENTIONAL NEGATIVE case — a run that MUST fail on the
/// metrics (e.g. a perfect trajectory but missing content), used to prove the evaluators
/// catch the failure. The default is `true`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct GoldenCase {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub description: String,
    #[serde(rename = "expectedTrajectory", default)]
    pub expected_trajectory: Vec<String>,
    #[serde(rename = "requiredKeys", default)]
    pub required_keys: Vec<String>,
    #[serde(rename = "expectPass", default = "default_true")]
    pub expect_pass: bool,
}

fn default_true() -> bool {
    true
}

pub fn load(path: &str) -> Option<GoldenCase> {
    match std::fs::read_to_string(path)
        .map_err(|e| e.to_string())
        .and_then(|json| serde_json::from_str::<GoldenCase>(&json).map_err(|e| e.to_string()))
    {
        Ok(case) => Some(case),
        Err(e) => {
            harness_log::error(&format!("[GoldenCaseStore] failed to load {path}: {e}"));
            None
        }
    }
}

/// Loads all `*.json` files from a directory, sorted by name, skipping invalid ones.
pub fn load_directory(directory: &str) -> Vec<GoldenCase> {
    let dir = std::path::Path::new(directory);
    if !dir.is_dir() {
        return Vec::new();
    }

    let mut paths: Vec<std::path::PathBuf> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|ext| ext == "json"))
        .collect();
    paths.sort();

    paths
        .into_iter()
        .filter_map(|p| load(p.to_str().unwrap()))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    // `load`'s failure path now writes to the cwd-relative `.harness/harness.log`
    // (harness_log::error) — tests that exercise it must isolate the process cwd into a
    // temp dir, or they'd leak a real `.harness/` into the crate's source tree.
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

    fn temp_dir() -> tempfile::TempDir {
        tempfile::tempdir().unwrap()
    }

    #[test]
    fn load_caso_valido_preenche_os_campos() {
        let dir = temp_dir();
        let path = dir.path().join("case.json");
        std::fs::write(
            &path,
            r#"{"id":"c1","description":"desc","expectedTrajectory":["start","plan"],"requiredKeys":["description"],"expectPass":false}"#,
        )
        .unwrap();

        let case = load(path.to_str().unwrap()).unwrap();

        assert_eq!(case.id, "c1");
        assert_eq!(
            case.expected_trajectory,
            vec!["start".to_string(), "plan".to_string()]
        );
        assert!(!case.expect_pass);
    }

    #[test]
    fn load_expect_pass_ausente_default_true() {
        let dir = temp_dir();
        let path = dir.path().join("case.json");
        std::fs::write(&path, r#"{"id":"c1","description":"d"}"#).unwrap();

        let case = load(path.to_str().unwrap()).unwrap();

        assert!(case.expect_pass);
    }

    #[test]
    fn load_arquivo_inexistente_retorna_none_sem_lancar() {
        let _guard = crate::test_support::lock_cwd();
        let _iso = Isolated::new();
        let dir = temp_dir();
        let path = dir.path().join("does-not-exist.json");

        assert!(load(path.to_str().unwrap()).is_none());
    }

    #[test]
    fn load_directory_ordena_por_nome_e_ignora_invalidos() {
        let _guard = crate::test_support::lock_cwd();
        let _iso = Isolated::new();
        let dir = temp_dir();
        std::fs::write(dir.path().join("b.json"), r#"{"id":"b"}"#).unwrap();
        std::fs::write(dir.path().join("a.json"), r#"{"id":"a"}"#).unwrap();
        std::fs::write(dir.path().join("c.json"), "this is not json").unwrap();

        let cases = load_directory(dir.path().to_str().unwrap());

        assert_eq!(cases.len(), 2);
        assert_eq!(cases[0].id, "a");
        assert_eq!(cases[1].id, "b");
    }

    #[test]
    fn load_directory_pasta_inexistente_retorna_vazio() {
        let dir = temp_dir();
        let missing = dir.path().join("does-not-exist");

        assert!(load_directory(missing.to_str().unwrap()).is_empty());
    }
}
