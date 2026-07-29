//! Batch evaluation over a golden set: instead of MMLU/HumanEval datasets, development
//! cases with the expected trajectory and keys. Purely deterministic (0 tokens): compares
//! the recorded evidence of each run against the case's expectation and aggregates the
//! pass rate.

use crate::evaluators::{self, Score};
use crate::golden_case_store::GoldenCase;
use crate::harness_state::HarnessState;
use crate::trace::TraceEntry;

/// Deterministic scores for a case. `passed` requires a full match across metrics; `ok`
/// is the suite's verdict — whether the case behaved as the golden set expected (an
/// intentional negative case is `ok` precisely when `passed` is false).
#[derive(Debug, Clone)]
pub struct CaseResult {
    pub id: String,
    pub scores: Vec<Score>,
    pub expected_pass: bool,
}

impl CaseResult {
    pub fn passed(&self) -> bool {
        self.scores.iter().all(|s| s.passed())
    }

    pub fn ok(&self) -> bool {
        self.passed() == self.expected_pass
    }
}

/// Batch aggregate: fraction of cases that behaved as expected (CI-ready).
#[derive(Debug, Clone)]
pub struct BatchResult {
    pub cases: Vec<CaseResult>,
}

impl BatchResult {
    pub fn total(&self) -> usize {
        self.cases.len()
    }

    pub fn passed_count(&self) -> usize {
        self.cases.iter().filter(|c| c.ok()).count()
    }

    pub fn pass_rate(&self) -> f64 {
        if self.total() == 0 {
            0.0
        } else {
            self.passed_count() as f64 / self.total() as f64
        }
    }
}

pub fn evaluate(
    golden: &GoldenCase,
    trace: &[TraceEntry],
    final_state: &HarnessState,
) -> CaseResult {
    CaseResult {
        id: golden.id.clone(),
        scores: vec![
            evaluators::trajectory(
                &golden.expected_trajectory,
                &evaluators::commands_of(trace, false),
            ),
            evaluators::step_budget(trace),
            evaluators::completeness(final_state, &golden.required_keys),
        ],
        expected_pass: golden.expect_pass,
    }
}

pub fn evaluate_all(runs: &[(GoldenCase, Vec<TraceEntry>, HarnessState)]) -> BatchResult {
    BatchResult {
        cases: runs
            .iter()
            .map(|(golden, trace, state)| evaluate(golden, trace, state))
            .collect(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::trace::trace_outcome;
    use std::collections::HashMap;

    fn happy_path() -> Vec<String> {
        [
            "start",
            "classify",
            "split",
            "acceptance",
            "estimate",
            "risks",
            "ready_check",
            "finalize",
        ]
        .iter()
        .map(|s| s.to_string())
        .collect()
    }

    fn keys() -> Vec<String> {
        ["description", "type", "verdict"]
            .iter()
            .map(|s| s.to_string())
            .collect()
    }

    fn golden(id: &str, expect_pass: bool) -> GoldenCase {
        GoldenCase {
            id: id.to_string(),
            description: String::new(),
            expected_trajectory: happy_path(),
            required_keys: keys(),
            expect_pass,
        }
    }

    fn trace_of(commands: &[&str]) -> Vec<TraceEntry> {
        let list: Vec<&str> = commands.to_vec();
        list.iter()
            .enumerate()
            .map(|(i, cmd)| TraceEntry {
                step: (i + 1) as i32,
                command: cmd.to_string(),
                outcome: if i == list.len() - 1 {
                    trace_outcome::STOP.to_string()
                } else {
                    trace_outcome::INSTRUCTION.to_string()
                },
                instruction_chars: 100,
                timestamp: String::new(),
                prev_hash: String::new(),
                label: String::new(),
            })
            .collect()
    }

    fn state_with(filled_keys: &[&str]) -> HarnessState {
        let mut data = HashMap::new();
        for k in filled_keys {
            data.insert(k.to_string(), "x".to_string());
        }
        HarnessState::new(filled_keys.len() as i32, data)
    }

    #[test]
    fn evaluate_run_perfeito_passa_todas_as_metricas() {
        let g = golden("ok", true);

        let result = evaluate(
            &g,
            &trace_of(&[
                "start",
                "classify",
                "split",
                "acceptance",
                "estimate",
                "risks",
                "ready_check",
                "finalize",
            ]),
            &state_with(&["description", "type", "verdict"]),
        );

        assert!(result.passed());
        assert!(
            result
                .scores
                .iter()
                .any(|s| s.metric == "trajectory" && s.passed())
        );
        assert!(
            result
                .scores
                .iter()
                .any(|s| s.metric == "completeness" && s.passed())
        );
        assert!(
            result
                .scores
                .iter()
                .any(|s| s.metric == "budget" && s.passed())
        );
    }

    #[test]
    fn evaluate_trajetoria_incompleta_reprova() {
        let g = golden("bad", true);

        let result = evaluate(
            &g,
            &trace_of(&["start", "classify", "finalize"]),
            &state_with(&["description", "type", "verdict"]),
        );

        assert!(!result.passed());
        assert!(
            result
                .scores
                .iter()
                .any(|s| s.metric == "trajectory" && !s.passed())
        );
    }

    #[test]
    fn evaluate_estado_incompleto_reprova() {
        let g = golden("missing", true);

        let result = evaluate(
            &g,
            &trace_of(&[
                "start",
                "classify",
                "split",
                "acceptance",
                "estimate",
                "risks",
                "ready_check",
                "finalize",
            ]),
            &state_with(&["description", "type"]),
        );

        assert!(!result.passed());
        assert!(
            result
                .scores
                .iter()
                .any(|s| s.metric == "completeness" && !s.passed())
        );
    }

    #[test]
    fn evaluate_all_agrega_taxa_de_acerto() {
        let good = golden("good", true);
        let bad = golden("bad", true);

        let batch = evaluate_all(&[
            (
                good,
                trace_of(&[
                    "start",
                    "classify",
                    "split",
                    "acceptance",
                    "estimate",
                    "risks",
                    "ready_check",
                    "finalize",
                ]),
                state_with(&["description", "type", "verdict"]),
            ),
            (
                bad,
                trace_of(&["start", "classify"]),
                state_with(&["description", "type", "verdict"]),
            ),
        ]);

        assert_eq!(batch.total(), 2);
        assert_eq!(batch.passed_count(), 1);
        assert_eq!(batch.pass_rate(), 0.5);
    }

    #[test]
    fn evaluate_all_lote_vazio_pass_rate_zero() {
        assert_eq!(evaluate_all(&[]).pass_rate(), 0.0);
    }

    #[test]
    fn evaluate_caso_negativo_intencional_que_reprova_nas_metricas_conta_como_ok() {
        let g = golden("negative", false);

        // missing "verdict"
        let result = evaluate(
            &g,
            &trace_of(&[
                "start",
                "classify",
                "split",
                "acceptance",
                "estimate",
                "risks",
                "ready_check",
                "finalize",
            ]),
            &state_with(&["description", "type"]),
        );

        assert!(!result.passed()); // fails on the metrics...
        assert!(result.ok()); // ...which is exactly the expected behavior
    }

    #[test]
    fn evaluate_caso_negativo_que_deixa_de_reprovar_conta_como_falha() {
        let g = golden("negative", false);

        // now passes on everything
        let result = evaluate(
            &g,
            &trace_of(&[
                "start",
                "classify",
                "split",
                "acceptance",
                "estimate",
                "risks",
                "ready_check",
                "finalize",
            ]),
            &state_with(&["description", "type", "verdict"]),
        );

        assert!(result.passed());
        assert!(!result.ok()); // a failure was expected and didn't happen
    }

    #[test]
    fn evaluate_all_caso_negativo_que_reprova_mantem_a_suite_verde() {
        let good = golden("good", true);
        let neg = golden("neg", false);

        let batch = evaluate_all(&[
            (
                good,
                trace_of(&[
                    "start",
                    "classify",
                    "split",
                    "acceptance",
                    "estimate",
                    "risks",
                    "ready_check",
                    "finalize",
                ]),
                state_with(&["description", "type", "verdict"]),
            ),
            (
                neg,
                trace_of(&[
                    "start",
                    "classify",
                    "split",
                    "acceptance",
                    "estimate",
                    "risks",
                    "ready_check",
                    "finalize",
                ]),
                state_with(&["description", "type"]),
            ),
        ]);

        assert_eq!(batch.passed_count(), 2); // ambos se comportaram como esperado
        assert_eq!(batch.pass_rate(), 1.0);
    }
}
