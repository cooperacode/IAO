//! Data contract exchanged between the driver (agent) and the state machine.
//! The model returns this envelope as JSON; the engine dispatches on `value`.
//!
//! There's no tokens field: the typical driver is an LLM with no access to its own
//! request's `usage`, so any self-reported count would be confabulated. The cost ceiling
//! only uses measures the engine attests to on its own (steps and instruction chars — see
//! `task_registry`); real tokens live in the caller's billing metadata.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::context_policy::ContextUsage;

/// Protocol signals carried in [`Envelope::type_`].
pub mod envelope_type {
    pub const TEXT: &str = "text";
    pub const TOOL: &str = "tool";
    pub const COMMAND: &str = "command";
    pub const ERROR: &str = "error";
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Envelope {
    #[serde(rename = "type")]
    pub type_: String,
    pub value: String,
    #[serde(default)]
    pub args: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub context: Option<HashMap<String, String>>,
    #[serde(rename = "contextUsage", default, skip_serializing_if = "Option::is_none")]
    pub context_usage: Option<ContextUsage>,
}

impl Envelope {
    pub fn new(type_: impl Into<String>, value: impl Into<String>, args: Vec<String>) -> Self {
        Self {
            type_: type_.into(),
            value: value.into(),
            args,
            context: None,
            context_usage: None,
        }
    }

    pub fn to_json(&self) -> String {
        // Compact (no whitespace) — the same wire format as .NET/Python.
        serde_json::to_string(self).expect("Envelope always serializes")
    }

    /// Tolerant parse: accepts markdown fences and surrounding text around the JSON object.
    pub fn parse(value: &str) -> Option<Envelope> {
        match Self::try_parse(value) {
            Ok(envelope) => Some(envelope),
            Err(err) => {
                // Diagnostic goes to stderr — stdout is the harness's transport channel
                // (the driver reads stdout as the next instruction) and must not be polluted.
                // The raw payload (truncated) is included because it's otherwise lost
                // forever: the inbox file gets overwritten by the driver's next attempt
                // before anyone can inspect what it actually sent.
                crate::harness_log::error(&format!(
                    "[Envelope] failed to parse: {err}. Raw payload: {}",
                    truncate(value, 500)
                ));
                None
            }
        }
    }

    fn try_parse(value: &str) -> Result<Envelope, String> {
        if value.trim().is_empty() {
            return Err("The JSON envelope cannot be null or empty.".to_string());
        }

        let sanitized = Self::sanitize(value);
        let root: Value = serde_json::from_str(&sanitized).map_err(|e| e.to_string())?;
        let root = root
            .as_object()
            .ok_or_else(|| "The envelope payload must be a JSON object.".to_string())?;

        let type_ = Self::string_field(root.get("type"))?;
        let envelope_value = Self::string_field(root.get("value"))?;

        let mut args = Vec::new();
        if let Some(Value::Array(items)) = root.get("args") {
            for item in items {
                let item = item
                    .as_str()
                    .ok_or_else(|| "each item of 'args' must be a string.".to_string())?;
                if !item.trim().is_empty() {
                    args.push(item.to_string());
                }
            }
        }

        let context = match root.get("context") {
            Some(Value::Object(map)) => {
                let mut ctx = HashMap::new();
                for (key, val) in map {
                    let val = val.as_str().ok_or_else(|| {
                        "each value of 'context' must be a string.".to_string()
                    })?;
                    ctx.insert(key.clone(), val.to_string());
                }
                Some(ctx)
            }
            _ => None,
        };

        let context_usage = root
            .get("contextUsage")
            .and_then(|value| serde_json::from_value::<ContextUsage>(value.clone()).ok());

        Ok(Envelope {
            type_,
            value: envelope_value,
            args,
            context,
            context_usage,
        })
    }

    /// Optional string field: absent/null becomes `""`; any other type is a parse error.
    fn string_field(field: Option<&Value>) -> Result<String, String> {
        match field {
            None | Some(Value::Null) => Ok(String::new()),
            Some(Value::String(s)) => Ok(s.clone()),
            Some(_) => Err("'type' and 'value' must be strings.".to_string()),
        }
    }

    /// Models often wrap the JSON in markdown fences (` ```json … ``` `) or add
    /// surrounding text. Normalizes to the raw JSON object before parsing.
    fn sanitize(value: &str) -> String {
        let mut v = value.trim().to_string();

        if v.starts_with("```") {
            if let Some(first_newline) = v.find('\n') {
                v = v[first_newline + 1..].to_string();
            }
            if let Some(closing_fence) = v.rfind("```") {
                v = v[..closing_fence].to_string();
            }
            v = v.trim().to_string();
        }

        if let (Some(start), Some(end)) = (v.find('{'), v.rfind('}')) {
            if end > start {
                v = v[start..=end].to_string();
            }
        }

        v
    }
}

fn truncate(value: &str, max_chars: usize) -> String {
    if value.chars().count() <= max_chars {
        return value.to_string();
    }
    let mut truncated: String = value.chars().take(max_chars).collect();
    truncated.push_str("...(truncated)");
    truncated
}

#[cfg(test)]
mod tests {
    use super::*;

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
    fn parse_json_valido_preenche_os_tres_campos() {
        let envelope =
            Envelope::parse(r#"{"type":"tool","value":"classify","args":["Login"]}"#).unwrap();

        assert_eq!(envelope.type_, "tool");
        assert_eq!(envelope.value, "classify");
        assert_eq!(envelope.args, vec!["Login".to_string()]);
    }

    #[test]
    fn parse_com_cerca_markdown_tolera() {
        let raw = "```json\n{\"type\":\"command\",\"value\":\"finalize\",\"args\":[\"Bug\"]}\n```";

        let envelope = Envelope::parse(raw).unwrap();

        assert_eq!(envelope.value, "finalize");
        assert_eq!(envelope.args, vec!["Bug".to_string()]);
    }

    #[test]
    fn parse_com_texto_ao_redor_extrai_o_objeto() {
        let raw =
            r#"Sure! Here it is: {"type":"text","value":"start","args":[]} — hope that helps."#;

        let envelope = Envelope::parse(raw).unwrap();

        assert_eq!(envelope.value, "start");
    }

    #[test]
    fn parse_sem_args_retorna_array_vazio() {
        let envelope = Envelope::parse(r#"{"type":"text","value":"start"}"#).unwrap();

        assert!(envelope.args.is_empty());
    }

    #[test]
    fn parse_ignora_args_vazios_ou_em_branco() {
        let envelope =
            Envelope::parse(r#"{"type":"tool","value":"x","args":["a","","  ","b"]}"#).unwrap();

        assert_eq!(envelope.args, vec!["a".to_string(), "b".to_string()]);
    }

    #[test]
    fn parse_entrada_invalida_retorna_none() {
        let _guard = crate::test_support::lock_cwd();
        let _iso = Isolated::new();

        for raw in [
            "",
            "   ",
            "{ \"type\": \"text\", \"value\": ",
            "this is not json",
            "[1,2,3]",
        ] {
            assert!(Envelope::parse(raw).is_none(), "expected None for {raw:?}");
        }
    }

    #[test]
    fn parse_entrada_invalida_grava_o_payload_cru_no_harness_log() {
        let _guard = crate::test_support::lock_cwd();
        let _iso = Isolated::new();

        // The raw driver payload is otherwise lost forever — the inbox file gets
        // overwritten by the next attempt before anyone can inspect what actually failed.
        Envelope::parse("this is not json");

        let content = std::fs::read_to_string(".harness/harness.log").unwrap();
        assert!(content.contains("this is not json"));
    }

    #[test]
    fn parse_payload_maior_que_o_teto_trunca_no_harness_log() {
        let _guard = crate::test_support::lock_cwd();
        let _iso = Isolated::new();

        let oversized = format!("{}not json", "x".repeat(600));
        Envelope::parse(&oversized);

        let content = std::fs::read_to_string(".harness/harness.log").unwrap();
        assert!(content.contains("...(truncated)"));
        assert!(!content.contains(&oversized));
    }

    #[test]
    fn to_json_faz_roundtrip() {
        let original = Envelope::new(
            envelope_type::COMMAND,
            "finalize",
            vec!["Epic".to_string()],
        );

        let roundtrip = Envelope::parse(&original.to_json()).unwrap();

        assert_eq!(original, roundtrip);
    }

    #[test]
    fn parse_com_context_preenche_o_dicionario() {
        let envelope = Envelope::parse(
            r#"{"type":"text","value":"start","context":{"driver":"claude code"}}"#,
        )
        .unwrap();

        assert_eq!(
            envelope.context.unwrap().get("driver").unwrap(),
            "claude code"
        );
    }

    #[test]
    fn parse_sem_context_retorna_none() {
        let envelope = Envelope::parse(r#"{"type":"text","value":"start"}"#).unwrap();

        assert!(envelope.context.is_none());
    }

    #[test]
    fn to_json_com_context_faz_roundtrip() {
        let mut original = Envelope::new(envelope_type::TEXT, "start", vec![]);
        original.context = Some(HashMap::from([(
            "driver".to_string(),
            "claude code".to_string(),
        )]));

        let roundtrip = Envelope::parse(&original.to_json()).unwrap();

        assert_eq!(original, roundtrip);
        assert_eq!(
            roundtrip.context.unwrap().get("driver").unwrap(),
            "claude code"
        );
    }

    #[test]
    fn to_json_sem_context_nao_emite_o_campo() {
        let envelope = Envelope::new(
            envelope_type::COMMAND,
            "finalize",
            vec!["Epic".to_string()],
        );

        assert!(!envelope.to_json().contains("context"));
    }

    #[test]
    fn context_usage_faz_roundtrip() {
        let mut original = Envelope::new(envelope_type::COMMAND, "start", vec![]);
        original.context_usage = Some(ContextUsage {
            schema: "iao.context.v1".to_string(),
            session_id: "s1".to_string(),
            context_window_tokens: 128_000,
            context_used_tokens: 84_000,
            source: "driver".to_string(),
        });

        let roundtrip = Envelope::parse(&original.to_json()).unwrap();

        assert_eq!(original, roundtrip);
    }

    #[test]
    fn parse_ignora_campos_desconhecidos() {
        // Extra fields (e.g. a "tokens" from an old driver) don't break the parse.
        let envelope =
            Envelope::parse(r#"{"type":"tool","value":"classify","args":["x"],"tokens":1234}"#)
                .unwrap();

        assert_eq!(envelope.value, "classify");
    }
}
