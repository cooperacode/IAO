//! Driver-agnostic adaptive context reset policy.

use serde::{Deserialize, Serialize};

use crate::{harness_config, state_store};

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct ContextUsage {
    #[serde(default)]
    pub schema: String,
    #[serde(rename = "sessionId", default)]
    pub session_id: String,
    #[serde(rename = "contextWindowTokens", default)]
    pub context_window_tokens: i32,
    #[serde(rename = "contextUsedTokens", default)]
    pub context_used_tokens: i32,
    #[serde(default)]
    pub source: String,
}

impl ContextUsage {
    pub fn from_environment() -> Option<Self> {
        let raw = std::env::var("HARNESS_CONTEXT_USAGE_JSON").ok()?;
        serde_json::from_str(raw.trim()).ok()
    }
}

const BOUNDARY_KEY: &str = "context_boundary_seen";
const FEATURES_KEY: &str = "context_features";
const RATIO_KEY: &str = "context_ratio";
const USAGE_SEEN_KEY: &str = "context_usage_seen";

pub fn observe(usage: Option<&ContextUsage>) {
    let Some(usage) = usage else { return };
    if usage.context_window_tokens <= 0 || usage.context_used_tokens < 0 {
        return;
    }
    let ratio = (usage.context_used_tokens as f64 / usage.context_window_tokens as f64).clamp(0.0, 1.0);
    state_store::set(RATIO_KEY, &format!("{ratio:.6}"));
    state_store::set(USAGE_SEEN_KEY, "true");
}

/// Emits the marker only when the policy requests a new driver context.
/// Retries and verification prompts do not call this function.
pub fn new_feature_prefix() -> String {
    let reset = should_reset();
    state_store::set(BOUNDARY_KEY, "true");
    if reset {
        state_store::set(FEATURES_KEY, "1");
        state_store::set(RATIO_KEY, "0");
        state_store::set(USAGE_SEEN_KEY, "false");
        return "=== NEW SESSION (clean context) ===\n\n".to_string();
    }

    let features = read_int(FEATURES_KEY).unwrap_or(0) + 1;
    state_store::set(FEATURES_KEY, &features.to_string());
    String::new()
}

fn should_reset() -> bool {
    let config = harness_config::current();
    match config.context_reset_mode.trim().to_ascii_lowercase().as_str() {
        "never" => return false,
        "per-feature" => return true,
        _ => {}
    }
    if state_store::get(BOUNDARY_KEY).is_none() {
        return true;
    }
    if let Some(ratio) = state_store::get(RATIO_KEY).and_then(|value| value.parse::<f64>().ok()) {
        if ratio >= config.context_reset_threshold {
            return true;
        }
    }
    state_store::get(USAGE_SEEN_KEY).as_deref() != Some("true")
        && read_int(FEATURES_KEY).unwrap_or(0) >= config.context_fallback_features
}

fn read_int(key: &str) -> Option<i32> {
    state_store::get(key).and_then(|value| value.parse::<i32>().ok()).filter(|value| *value >= 0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::lock_cwd;

    #[test]
    fn politica_adaptativa_reseta_no_primeiro_e_no_threshold() {
        let _guard = lock_cwd();
        let dir = tempfile::tempdir().unwrap();
        let previous = std::env::current_dir().unwrap();
        std::env::set_current_dir(dir.path()).unwrap();
        std::fs::write(
            "harness.json",
            r#"{"contextResetMode":"adaptive","contextResetThreshold":0.7,"contextFallbackFeatures":1}"#,
        )
        .unwrap();
        harness_config::reset();
        state_store::reset();

        assert!(new_feature_prefix().starts_with("=== NEW SESSION"));
        observe(Some(&ContextUsage {
            context_window_tokens: 100,
            context_used_tokens: 50,
            ..ContextUsage::default()
        }));
        assert!(new_feature_prefix().is_empty());
        observe(Some(&ContextUsage {
            context_window_tokens: 100,
            context_used_tokens: 80,
            ..ContextUsage::default()
        }));
        assert!(new_feature_prefix().starts_with("=== NEW SESSION"));

        state_store::reset();
        harness_config::reset();
        std::env::set_current_dir(previous).unwrap();
    }

    #[test]
    fn context_usage_le_do_hook_de_ambiente() {
        let _guard = crate::test_support::lock_cwd();
        unsafe {
            std::env::set_var(
                "HARNESS_CONTEXT_USAGE_JSON",
                r#"{"contextWindowTokens":100,"contextUsedTokens":70,"source":"host"}"#,
            );
        }

        let usage = ContextUsage::from_environment().unwrap();

        assert_eq!(usage.context_window_tokens, 100);
        assert_eq!(usage.context_used_tokens, 70);
        unsafe { std::env::remove_var("HARNESS_CONTEXT_USAGE_JSON") };
    }
}
