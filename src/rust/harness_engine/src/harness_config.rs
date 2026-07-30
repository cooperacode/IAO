//! Fixed harness settings, externalized in a `harness.json` at the repo root. Centralizing
//! them here lets each flow/environment tune the ceilings without a recompile. Missing or
//! unreadable → falls back to the defaults (same tolerance as `StateStore`: config is
//! optional input, it must not bring down the run).

use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::path_resolver;

const FILE_PATH: &str = "harness.json";

// Hard ceiling on timeout_ms, regardless of source (harness.json OR the env var below).
// harness.json lives in the working directory that the supervised agent itself controls:
// without this ceiling, the agent could edit the file to grant itself an arbitrarily high
// timeout and never get cut off by the time guard (see task_registry).
const MAX_ALLOWED_TIMEOUT_MS: i32 = 5 * 60_000;

// When set, overrides harness.json's timeout_ms. Unlike the file, the env var is set by
// the parent process that invokes each harness step — outside the working directory the
// supervised agent controls — so it can't be self-edited by the very agent the timeout is
// meant to contain.
const TIMEOUT_MS_ENV_VAR: &str = "HARNESS_TIMEOUT_MS";

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
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
    #[serde(rename = "contextResetMode", default)]
    pub context_reset_mode: String,
    #[serde(rename = "contextResetThreshold", default)]
    pub context_reset_threshold: f64,
    #[serde(rename = "contextFallbackFeatures", default)]
    pub context_fallback_features: i32,
}

// Step ceiling: prevents an infinite loop that would burn tokens indefinitely.
// max_instruction_chars = 0 turns off the cost ceiling (only the step one applies).
// timeout_ms = 0 turns off the per-step time guard (same convention as the cost one). The
// enabled value lives in the shipped harness.json, NOT here: if the default were > 0, a
// harness.json that omitted the field (deserializes to 0) could never mean "off".
pub fn default_config() -> HarnessConfig {
    HarnessConfig {
        max_steps: 12,
        max_instruction_chars: 0,
        docs_max_chars: 40_000,
        docs_folder: "docs".to_string(),
        timeout_ms: 0,
        context_reset_mode: "adaptive".to_string(),
        context_reset_threshold: 0.70,
        context_fallback_features: 1,
    }
}

static CURRENT: Mutex<Option<HarnessConfig>> = Mutex::new(None);

/// Re-reads `harness.json` from disk; any failure returns the defaults.
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
                eprintln!("[HarnessConfig] failed to load; using defaults: {e}");
                config = default_config();
            }
        }
    }

    normalize(apply_timeout_env_override(config))
}

/// Forces a re-read of `harness.json` — for tests and long-lived drivers.
pub fn reload() -> HarnessConfig {
    let config = load();
    *CURRENT.lock().unwrap() = Some(config.clone());
    config
}

/// Clears the cache without re-reading — the next `current()` reads it on demand.
pub fn reset() {
    *CURRENT.lock().unwrap() = None;
}

/// Loaded once per process (each harness invocation is a new process, so "once" =
/// "per loop iteration"). Static readers consume it from here without needing to
/// receive the config as a parameter.
pub fn current() -> HarnessConfig {
    let mut guard = CURRENT.lock().unwrap();
    if guard.is_none() {
        *guard = Some(load());
    }
    guard.clone().unwrap()
}

// See TIMEOUT_MS_ENV_VAR. Missing/invalid is silently ignored — same tolerance as the
// rest of the config: it's optional input, it must not bring down the run.
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

// A partial harness.json deserializes missing fields as 0/"" (`#[serde(default)]`). Zero
// is valid only where it means "off" (cost ceilings); elsewhere, a missing field = default.
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
        context_reset_mode: match config.context_reset_mode.trim().to_ascii_lowercase().as_str() {
            "adaptive" | "per-feature" | "never" => config.context_reset_mode.trim().to_ascii_lowercase(),
            _ => default.context_reset_mode,
        },
        context_reset_threshold: if config.context_reset_threshold <= 0.0 {
            default.context_reset_threshold
        } else {
            config.context_reset_threshold.clamp(0.1, 1.0)
        },
        context_fallback_features: if config.context_fallback_features > 0 {
            config.context_fallback_features
        } else {
            default.context_fallback_features
        },
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
            // SAFETY: serialized by `lock_cwd()` — no other thread reads/writes env
            // vars while this test runs.
            unsafe { std::env::remove_var(TIMEOUT_MS_ENV_VAR) };
            Self {
                _dir: dir,
                previous,
            }
        }
    }

    impl Drop for Isolated {
        fn drop(&mut self) {
            // SAFETY: see `Isolated::new`.
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

        // A negative value is normalized to 0 (off), like the cost ceiling.
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

        std::fs::write("harness.json", "{ this is not json ").unwrap();

        assert_eq!(load(), default_config());
    }

    #[test]
    fn load_timeout_acima_do_teto_clampa_no_maximo_permitido() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // harness.json lives in the supervised agent's working directory: even if it
        // edits the file to grant itself a huge timeout, the hard ceiling prevails.
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
        unsafe { std::env::set_var(TIMEOUT_MS_ENV_VAR, "not a number") };

        assert_eq!(load().timeout_ms, 1000);
    }
}
