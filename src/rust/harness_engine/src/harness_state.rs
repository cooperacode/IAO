//! State persisted across invocations: step counter + accumulated domain data.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HarnessState {
    #[serde(default)]
    pub step: i32,
    #[serde(default)]
    pub data: HashMap<String, String>,
    // Accumulated cost for the run, the input to the cost ceiling (see task_registry).
    #[serde(rename = "costChars", default)]
    pub cost_chars: i32,
    // Driver context (e.g. {"driver": "claude code"}) captured from the `start` envelope —
    // survives across invocations so prompt_formatter can reinject it into every output
    // without each task having to pass it along manually.
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
