//! Deterministic evaluators (Exact Match, Regex, Trajectory) — the part of the Evaluator
//! that does NOT need an LLM. Run in-process over the `Trace` and `HarnessState`, cost
//! zero tokens, and act as a gate: only when they pass is it worth escalating to the LLM
//! judge (savings under the token budget constraint).

use regex::Regex;

use crate::harness_state::HarnessState;
use crate::trace::{TraceEntry, trace_outcome};

/// Score for a metric in `[0,1]`. `passed` requires a full match.
#[derive(Debug, Clone, PartialEq)]
pub struct Score {
    pub metric: String,
    pub value: f64,
    pub detail: String,
}

impl Score {
    pub fn new(metric: impl Into<String>, value: f64, detail: impl Into<String>) -> Self {
        Self {
            metric: metric.into(),
            value,
            detail: detail.into(),
        }
    }

    pub fn passed(&self) -> bool {
        self.value >= 1.0
    }
}

pub fn exact_match(expected: &str, actual: &str) -> Score {
    let value = if expected.trim() == actual.trim() {
        1.0
    } else {
        0.0
    };
    Score::new(
        "exact_match",
        value,
        format!("expected=\"{expected}\" got=\"{actual}\""),
    )
}

pub fn matches_regex(pattern: &str, actual: &str) -> Score {
    let value = Regex::new(pattern)
        .map(|re| re.is_match(actual))
        .unwrap_or(false);
    Score::new("regex", if value { 1.0 } else { 0.0 }, pattern)
}

/// Fraction of the expected prefix that matched, in order. A step out of sequence cuts
/// the count off right there — trajectory is about the path, not the set.
pub fn trajectory(expected: &[String], actual: &[String]) -> Score {
    let mut matched = 0;
    for i in 0..expected.len().min(actual.len()) {
        if expected[i] != actual[i] {
            break;
        }
        matched += 1;
    }

    let value = if expected.is_empty() {
        1.0
    } else {
        matched as f64 / expected.len() as f64
    };
    Score::new(
        "trajectory",
        value,
        format!("{matched}/{} steps in the expected order", expected.len()),
    )
}

/// Were all the expected domain keys filled in the final state?
pub fn completeness(state: &HarnessState, required_keys: &[String]) -> Score {
    let filled = required_keys
        .iter()
        .filter(|k| {
            !state
                .data
                .get(*k)
                .map(|v| v.trim().is_empty())
                .unwrap_or(true)
        })
        .count();
    let value = if required_keys.is_empty() {
        1.0
    } else {
        filled as f64 / required_keys.len() as f64
    };
    Score::new(
        "completeness",
        value,
        format!("{filled}/{} keys filled", required_keys.len()),
    )
}

/// Finished at `stop` without hitting the step ceiling or the time ceiling (`timeout`) —
/// both would be indistinguishable from a simply-incomplete trajectory if they weren't
/// checked separately.
pub fn step_budget(trace: &[TraceEntry]) -> Score {
    let hit_budget = trace.iter().any(|e| e.outcome == trace_outcome::BUDGET);
    let hit_timeout = trace.iter().any(|e| e.outcome == trace_outcome::TIMEOUT);
    let terminated = trace.iter().any(|e| e.outcome == trace_outcome::STOP);

    let value = if !hit_budget && !hit_timeout && terminated {
        1.0
    } else {
        0.0
    };
    let detail = if hit_budget {
        "cut off by the step ceiling"
    } else if hit_timeout {
        "cut off by the time ceiling (timeout)"
    } else if terminated {
        "completed within budget"
    } else {
        "did not finish"
    };
    Score::new("budget", value, detail)
}

/// Trace commands in order, ignoring corrective-error round-trips by default.
pub fn commands_of(trace: &[TraceEntry], include_errors: bool) -> Vec<String> {
    trace
        .iter()
        .filter(|e| include_errors || e.outcome != trace_outcome::ERROR)
        .map(|e| e.command.clone())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn strings(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    fn entry(step: i32, command: &str, outcome: &str, chars: i32) -> TraceEntry {
        TraceEntry {
            step,
            command: command.to_string(),
            outcome: outcome.to_string(),
            instruction_chars: chars,
            timestamp: String::new(),
            prev_hash: String::new(),
            label: String::new(),
            context_window_tokens: None,
            context_used_tokens: None,
            context_ratio: None,
        }
    }

    #[test]
    fn exact_match_normaliza_espacos_e_compara_conteudo() {
        assert_eq!(exact_match("Bug", "Bug").value, 1.0);
        assert_eq!(exact_match("Bug", "  Bug  ").value, 1.0);
        assert_eq!(exact_match("Bug", "Epic").value, 0.0);
    }

    #[test]
    fn matches_regex_avalia_o_padrao() {
        assert!(matches_regex(r"^\d+\s*pts$", "13 pts").passed());
        assert!(!matches_regex(r"^\d+\s*pts$", "treze").passed());
    }

    #[test]
    fn trajectory_caminho_identico_pontua_cheio() {
        let expected = strings(&["start", "classify", "finalize"]);

        let score = trajectory(&expected, &strings(&["start", "classify", "finalize"]));

        assert!(score.passed());
        assert_eq!(score.value, 1.0);
    }

    #[test]
    fn trajectory_diverge_no_meio_conta_so_o_prefixo_em_ordem() {
        let expected = strings(&["start", "classify", "split", "finalize"]);

        let score = trajectory(&expected, &strings(&["start", "classify", "finalize"]));

        assert_eq!(score.value, 0.5);
        assert!(!score.passed());
    }

    #[test]
    fn trajectory_esperado_vazio_pontua_cheio() {
        assert!(trajectory(&[], &[]).passed());
    }

    #[test]
    fn completeness_conta_chaves_preenchidas() {
        let mut state = HarnessState::new(3, Default::default());
        state
            .data
            .insert("description".to_string(), "Login".to_string());
        state.data.insert("type".to_string(), "Feature".to_string());
        state
            .data
            .insert("stories".to_string(), "   ".to_string()); // blank doesn't count

        let score = completeness(&state, &strings(&["description", "type", "stories"]));

        assert!((score.value - 2.0 / 3.0).abs() < 1e-6);
        assert!(!score.passed());
    }

    #[test]
    fn step_budget_concluiu_com_stop_passa() {
        let trace = vec![
            entry(1, "start", trace_outcome::INSTRUCTION, 100),
            entry(2, "finalize", trace_outcome::STOP, 4),
        ];

        assert!(step_budget(&trace).passed());
    }

    #[test]
    fn step_budget_cortado_pelo_teto_falha() {
        let trace = vec![
            entry(1, "classify", trace_outcome::INSTRUCTION, 100),
            entry(13, "classify", trace_outcome::BUDGET, 4),
        ];

        assert!(!step_budget(&trace).passed());
    }

    #[test]
    fn step_budget_cortado_pelo_timeout_falha_e_distingue_de_nao_terminou() {
        let trace = vec![
            entry(1, "classify", trace_outcome::INSTRUCTION, 100),
            entry(2, "slow", trace_outcome::TIMEOUT, 4),
        ];

        let score = step_budget(&trace);

        assert!(!score.passed());
        assert_eq!(score.detail, "cut off by the time ceiling (timeout)");
    }

    #[test]
    fn commands_of_ignora_voltas_de_erro_por_padrao() {
        let trace = vec![
            entry(1, "start", trace_outcome::INSTRUCTION, 100),
            entry(2, "(unparsed)", trace_outcome::ERROR, 200),
            entry(3, "classify", trace_outcome::INSTRUCTION, 150),
        ];

        assert_eq!(commands_of(&trace, false), strings(&["start", "classify"]));
        assert_eq!(
            commands_of(&trace, true),
            strings(&["start", "(unparsed)", "classify"])
        );
    }
}
