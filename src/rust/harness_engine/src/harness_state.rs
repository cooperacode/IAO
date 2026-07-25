//! Estado persistido entre invocações: contador de passos + dados acumulados do domínio.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HarnessState {
    #[serde(default)]
    pub step: i32,
    #[serde(default)]
    pub data: HashMap<String, String>,
    // Custo acumulado do run, insumo do teto de custo (ver task_registry).
    #[serde(rename = "costChars", default)]
    pub cost_chars: i32,
    // Contexto do driver (ex.: {"driver": "claude code"}) capturado no envelope `start` —
    // sobrevive entre invocações para que prompt_formatter possa reinjetá-lo em toda saída
    // sem que cada task o repasse manualmente.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub context: Option<HashMap<String, String>>,
}

impl HarnessState {
    pub fn new(step: i32, data: HashMap<String, String>) -> Self {
        Self {
            step,
            data,
            cost_chars: 0,
            context: None,
        }
    }
}
