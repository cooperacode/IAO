//! Contrato de dados trafegado entre o driver (agente) e a máquina de estados.
//! O modelo devolve este envelope como JSON; a engine faz o dispatch por `value`.
//!
//! Não há campo de tokens: o driver típico é um LLM sem acesso ao `usage` da própria
//! requisição, então qualquer contagem auto-reportada seria confabulada. O teto de custo
//! usa apenas medidas que a engine atesta sozinha (passos e chars de instrução — ver
//! `task_registry`); tokens reais vivem nos metadados de billing do caller.

use std::collections::HashMap;
use std::io::Write;

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// Sinais de protocolo carregados em [`Envelope::type_`].
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
}

impl Envelope {
    pub fn new(type_: impl Into<String>, value: impl Into<String>, args: Vec<String>) -> Self {
        Self {
            type_: type_.into(),
            value: value.into(),
            args,
            context: None,
        }
    }

    pub fn to_json(&self) -> String {
        // Compacto (sem espaços) — mesmo formato de fio que .NET/Python.
        serde_json::to_string(self).expect("Envelope sempre serializa")
    }

    /// Parse tolerante: aceita cercas markdown e texto ao redor do objeto JSON.
    pub fn parse(value: &str) -> Option<Envelope> {
        match Self::try_parse(value) {
            Ok(envelope) => Some(envelope),
            Err(err) => {
                // Diagnóstico vai para stderr — stdout é o canal de transporte do harness
                // (o driver lê stdout como a próxima instrução) e não pode ser poluído.
                let _ = writeln!(std::io::stderr(), "{err}");
                None
            }
        }
    }

    fn try_parse(value: &str) -> Result<Envelope, String> {
        if value.trim().is_empty() {
            return Err("O envelope JSON não pode ser nulo ou vazio.".to_string());
        }

        let sanitized = Self::sanitize(value);
        let root: Value = serde_json::from_str(&sanitized).map_err(|e| e.to_string())?;
        let root = root
            .as_object()
            .ok_or_else(|| "O payload do envelope deve ser um objeto JSON.".to_string())?;

        let type_ = Self::string_field(root.get("type"))?;
        let envelope_value = Self::string_field(root.get("value"))?;

        let mut args = Vec::new();
        if let Some(Value::Array(items)) = root.get("args") {
            for item in items {
                let item = item
                    .as_str()
                    .ok_or_else(|| "cada item de 'args' deve ser uma string.".to_string())?;
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
                        "cada valor de 'context' deve ser uma string.".to_string()
                    })?;
                    ctx.insert(key.clone(), val.to_string());
                }
                Some(ctx)
            }
            _ => None,
        };

        Ok(Envelope {
            type_,
            value: envelope_value,
            args,
            context,
        })
    }

    /// Campo string opcional: ausente/null vira `""`; qualquer outro tipo é erro de parse.
    fn string_field(field: Option<&Value>) -> Result<String, String> {
        match field {
            None | Some(Value::Null) => Ok(String::new()),
            Some(Value::String(s)) => Ok(s.clone()),
            Some(_) => Err("'type' e 'value' devem ser strings.".to_string()),
        }
    }

    /// Modelos frequentemente embrulham o JSON em cercas markdown (` ```json … ``` `)
    /// ou adicionam texto ao redor. Normaliza para o objeto JSON bruto antes do parse.
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

#[cfg(test)]
mod tests {
    use super::*;

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
            r#"Claro! Aqui está: {"type":"text","value":"start","args":[]} — espero ter ajudado."#;

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
        for raw in [
            "",
            "   ",
            "{ \"type\": \"text\", \"value\": ",
            "isso não é json",
            "[1,2,3]",
        ] {
            assert!(Envelope::parse(raw).is_none(), "esperava None para {raw:?}");
        }
    }

    #[test]
    fn to_json_faz_roundtrip() {
        let original = Envelope::new(
            envelope_type::COMMAND,
            "finalize",
            vec!["Épico".to_string()],
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
            vec!["Épico".to_string()],
        );

        assert!(!envelope.to_json().contains("context"));
    }

    #[test]
    fn parse_ignora_campos_desconhecidos() {
        // Campos extras (ex.: um "tokens" de driver antigo) não derrubam o parse.
        let envelope =
            Envelope::parse(r#"{"type":"tool","value":"classify","args":["x"],"tokens":1234}"#)
                .unwrap();

        assert_eq!(envelope.value, "classify");
    }
}
