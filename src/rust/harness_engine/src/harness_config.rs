//! Variáveis fixas do harness, externalizadas num `harness.json` na raiz do repo.
//! Centralizá-las aqui deixa cada flow/ambiente ajustar os tetos sem recompilar. Ausente
//! ou ilegível → cai nos defaults (mesma tolerância do `StateStore`: config é insumo
//! opcional, não pode derrubar o run).

use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::path_resolver;

const FILE_PATH: &str = "harness.json";

// Teto duro do timeout_ms, independente da fonte (harness.json OU a env var abaixo).
// harness.json vive no working directory que o próprio agente supervisionado controla:
// sem este teto, o agente poderia editar o arquivo para se auto-conceder um timeout
// arbitrariamente alto e nunca ser cortado pela guarda de tempo (ver task_registry).
const MAX_ALLOWED_TIMEOUT_MS: i32 = 5 * 60_000;

// Quando definida, sobrepõe o timeout_ms do harness.json. Ao contrário do arquivo, a env
// var é definida pelo processo pai que invoca cada passo do harness — fora do working
// directory que o agente supervisionado controla — então não pode ser auto-editada pelo
// mesmo agente que o timeout deveria conter.
const TIMEOUT_MS_ENV_VAR: &str = "HARNESS_TIMEOUT_MS";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HarnessConfig {
    #[serde(rename = "maxSteps", default)]
    pub max_steps: i32,
    #[serde(rename = "maxInstructionChars", default)]
    pub max_instruction_chars: i32,
    #[serde(rename = "docsMaxChars", default)]
    pub docs_max_chars: i32,
    #[serde(rename = "docsFolder", default)]
    pub docs_folder: String,
    #[serde(rename = "timeoutMs", default)]
    pub timeout_ms: i32,
}

// Teto de passos: impede loop infinito que queimaria tokens indefinidamente.
// max_instruction_chars = 0 desliga o teto de custo (só o de passos vale).
// timeout_ms = 0 desliga a guarda de tempo por passo (mesma convenção do custo). O valor
// ligado vive no harness.json shipado, NÃO aqui: se o default fosse > 0, um harness.json
// que omitisse o campo (deserializa 0) nunca conseguiria significar "desligado".
pub fn default_config() -> HarnessConfig {
    HarnessConfig {
        max_steps: 12,
        max_instruction_chars: 0,
        docs_max_chars: 40_000,
        docs_folder: "docs".to_string(),
        timeout_ms: 0,
    }
}

static CURRENT: Mutex<Option<HarnessConfig>> = Mutex::new(None);

/// Relê o `harness.json` do disco; qualquer falha devolve os defaults.
pub fn load() -> HarnessConfig {
    let mut config = default_config();

    let path = path_resolver::resolve(FILE_PATH);
    let path = std::path::Path::new(&path);
    if path.exists() {
        let loaded = std::fs::read_to_string(path)
            .map_err(|e| e.to_string())
            .and_then(|json| {
                serde_json::from_str::<HarnessConfig>(&json).map_err(|e| e.to_string())
            });

        match loaded {
            Ok(parsed) => config = parsed,
            Err(e) => {
                eprintln!("[HarnessConfig] falha ao carregar; usando defaults: {e}");
                config = default_config();
            }
        }
    }

    normalize(apply_timeout_env_override(config))
}

/// Força a releitura do `harness.json` — para testes e drivers de longa vida.
pub fn reload() -> HarnessConfig {
    let config = load();
    *CURRENT.lock().unwrap() = Some(config.clone());
    config
}

/// Limpa o cache sem reler — a próxima `current()` relê sob demanda.
pub fn reset() {
    *CURRENT.lock().unwrap() = None;
}

/// Carregada uma vez por processo (cada invocação do harness é um processo novo, então
/// "uma vez" = "por volta do loop"). Leitores estáticos consomem daqui sem precisar
/// receber a config por parâmetro.
pub fn current() -> HarnessConfig {
    let mut guard = CURRENT.lock().unwrap();
    if guard.is_none() {
        *guard = Some(load());
    }
    guard.clone().unwrap()
}

// Ver TIMEOUT_MS_ENV_VAR. Ausente/inválida é ignorada silenciosamente — mesma
// tolerância do resto da config: é insumo opcional, não pode derrubar o run.
fn apply_timeout_env_override(config: HarnessConfig) -> HarnessConfig {
    match std::env::var(TIMEOUT_MS_ENV_VAR) {
        Ok(raw) => match raw.trim().parse::<i32>() {
            Ok(timeout_ms) => HarnessConfig {
                timeout_ms,
                ..config
            },
            Err(_) => config,
        },
        Err(_) => config,
    }
}

// Um harness.json parcial deserializa os campos ausentes como 0/"" (`#[serde(default)]`).
// Zero é válido só onde significa "desligado" (tetos de custo); nos demais, campo
// ausente = default.
fn normalize(config: HarnessConfig) -> HarnessConfig {
    let default = default_config();
    HarnessConfig {
        max_steps: if config.max_steps > 0 {
            config.max_steps
        } else {
            default.max_steps
        },
        max_instruction_chars: config.max_instruction_chars.max(0),
        docs_max_chars: if config.docs_max_chars > 0 {
            config.docs_max_chars
        } else {
            default.docs_max_chars
        },
        docs_folder: if config.docs_folder.trim().is_empty() {
            default.docs_folder
        } else {
            config.docs_folder
        },
        timeout_ms: config.timeout_ms.clamp(0, MAX_ALLOWED_TIMEOUT_MS),
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
            // SAFETY: serializado por `lock_cwd()` — nenhuma outra thread lê/escreve env
            // vars enquanto este teste roda.
            unsafe { std::env::remove_var(TIMEOUT_MS_ENV_VAR) };
            Self {
                _dir: dir,
                previous,
            }
        }
    }

    impl Drop for Isolated {
        fn drop(&mut self) {
            // SAFETY: ver `Isolated::new`.
            unsafe { std::env::remove_var(TIMEOUT_MS_ENV_VAR) };
            let _ = std::env::set_current_dir(&self.previous);
        }
    }

    #[test]
    fn load_sem_arquivo_usa_defaults() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let config = load();

        assert_eq!(config, default_config());
        assert_eq!(config.max_steps, 12);
        assert_eq!(config.max_instruction_chars, 0);
        assert_eq!(config.timeout_ms, 0);
    }

    #[test]
    fn load_com_timeout_le_e_normaliza() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::write("harness.json", r#"{"timeoutMs":30000}"#).unwrap();
        assert_eq!(load().timeout_ms, 30000);

        // Valor negativo é normalizado para 0 (desligado), como o teto de custo.
        std::fs::write("harness.json", r#"{"timeoutMs":-5}"#).unwrap();
        assert_eq!(load().timeout_ms, 0);
    }

    #[test]
    fn load_com_arquivo_usa_os_valores_do_arquivo() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::write(
            "harness.json",
            r#"{"maxSteps":5,"maxInstructionChars":20000,"docsMaxChars":10000,"docsFolder":"specs"}"#,
        )
        .unwrap();

        let config = load();

        assert_eq!(config.max_steps, 5);
        assert_eq!(config.max_instruction_chars, 20000);
        assert_eq!(config.docs_max_chars, 10000);
        assert_eq!(config.docs_folder, "specs");
    }

    #[test]
    fn load_arquivo_parcial_completa_com_defaults() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::write("harness.json", r#"{"maxInstructionChars":8000}"#).unwrap();

        let config = load();

        assert_eq!(config.max_instruction_chars, 8000);
        assert_eq!(config.max_steps, default_config().max_steps);
        assert_eq!(config.docs_max_chars, default_config().docs_max_chars);
        assert_eq!(config.docs_folder, default_config().docs_folder);
    }

    #[test]
    fn load_arquivo_invalido_cai_nos_defaults_sem_lancar() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::write("harness.json", "{ isso não é json ").unwrap();

        assert_eq!(load(), default_config());
    }

    #[test]
    fn load_timeout_acima_do_teto_clampa_no_maximo_permitido() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // harness.json vive no working directory do agente supervisionado: mesmo que ele
        // edite o arquivo para se auto-conceder um timeout enorme, o teto duro prevalece.
        std::fs::write("harness.json", r#"{"timeoutMs":99999999}"#).unwrap();

        assert_eq!(load().timeout_ms, 5 * 60_000);
    }

    #[test]
    fn load_com_env_var_sobrepoe_o_timeout_do_arquivo() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::write("harness.json", r#"{"timeoutMs":1000}"#).unwrap();
        // SAFETY: ver `Isolated::new`.
        unsafe { std::env::set_var(TIMEOUT_MS_ENV_VAR, "2000") };

        assert_eq!(load().timeout_ms, 2000);
    }

    #[test]
    fn load_env_var_tambem_respeita_o_teto() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // SAFETY: ver `Isolated::new`.
        unsafe { std::env::set_var(TIMEOUT_MS_ENV_VAR, "99999999") };

        assert_eq!(load().timeout_ms, 5 * 60_000);
    }

    #[test]
    fn load_env_var_invalida_e_ignorada() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::write("harness.json", r#"{"timeoutMs":1000}"#).unwrap();
        // SAFETY: ver `Isolated::new`.
        unsafe { std::env::set_var(TIMEOUT_MS_ENV_VAR, "não é número") };

        assert_eq!(load().timeout_ms, 1000);
    }
}
