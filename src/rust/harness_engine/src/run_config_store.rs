//! Persiste `verify_cmd`/`target_dir` (capturados uma vez pelo `plan`) em
//! `.harness/run_config.json` — fora de `state.json` de propósito. `task_registry` reseta
//! `state.json` incondicionalmente a cada `start`, antes de qualquer código de domínio
//! rodar; um run retomado ainda precisa desses dois valores para `smoke`/`verify`
//! funcionarem, então eles têm que sobreviver a esse reset.

use serde::{Deserialize, Serialize};

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/run_config.json";

/// Comando de verificação e diretório-alvo capturados pelo `plan`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RunConfig {
    #[serde(rename = "verifyCmd", default)]
    pub verify_cmd: String,
    #[serde(rename = "targetDir", default = "default_target_dir")]
    pub target_dir: String,
}

fn default_target_dir() -> String {
    ".".to_string()
}

impl Default for RunConfig {
    fn default() -> Self {
        Self {
            verify_cmd: String::new(),
            target_dir: default_target_dir(),
        }
    }
}

/// Grava a configuração do run — mesmo ciclo de vida da `feature_list.json` (escrita
/// pelo `plan`, apagada só quando `start` decide que não há run para retomar).
pub fn write(config: &RunConfig) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[RunConfigStore] falha ao gravar: {e}");
        return;
    }
    match serde_json::to_string(config) {
        Ok(json) => {
            if let Err(e) = std::fs::write(FILE_PATH, json) {
                eprintln!("[RunConfigStore] falha ao gravar: {e}");
            }
        }
        Err(e) => eprintln!("[RunConfigStore] falha ao gravar: {e}"),
    }
}

/// Lê a configuração persistida, ou os defaults se nada foi gravado ainda.
pub fn load() -> RunConfig {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        let loaded = std::fs::read_to_string(p)
            .map_err(|e| e.to_string())
            .and_then(|json| serde_json::from_str::<RunConfig>(&json).map_err(|e| e.to_string()));

        match loaded {
            Ok(config) => return config,
            Err(e) => eprintln!("[RunConfigStore] falha ao carregar: {e}"),
        }
    }
    RunConfig::default()
}

/// Apaga num run genuinamente novo — em par com `feature_store::reset`.
pub fn reset() {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        if let Err(e) = std::fs::remove_file(p) {
            eprintln!("[RunConfigStore] falha ao limpar: {e}");
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
        });

        let loaded = load();

        assert_eq!(loaded.verify_cmd, "npm test");
        assert_eq!(loaded.target_dir, "app");
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
