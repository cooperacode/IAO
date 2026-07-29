//! Cheap, deterministic predicates that validate whether the value returned by the driver
//! meets the task's expectation — BEFORE persisting it and moving the flow forward. On
//! failure, `TaskRegistry` returns a typed corrective error and the driver resends (a
//! corrective loop, not a silent stall).
//!
//! Deep semantic validation is still the LLM judge's job at evaluation time; what lives
//! here is only what's checkable in code, at zero token cost.

use regex::RegexBuilder;

use crate::envelope::Envelope;

/// Result of a contextual validation: ok, or the reason for rejection (for the corrective error).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ValidationResult {
    pub ok: bool,
    pub reason: String,
}

impl ValidationResult {
    pub fn pass() -> Self {
        Self {
            ok: true,
            reason: String::new(),
        }
    }

    pub fn fail(reason: impl Into<String>) -> Self {
        Self {
            ok: false,
            reason: reason.into(),
        }
    }
}

pub type Validator = Box<dyn Fn(&Envelope) -> ValidationResult + Send + Sync>;

/// The first arg exists and is not empty/whitespace.
pub fn not_empty(expectation: &str) -> Validator {
    let expectation = expectation.to_string();
    Box::new(move |envelope| {
        if !first_arg(envelope).is_empty() {
            ValidationResult::pass()
        } else {
            ValidationResult::fail(format!(
                "The expected argument came back empty. Expected: {expectation}."
            ))
        }
    })
}

/// The first arg has at least `count` non-empty lines (counting literal `\n`).
pub fn min_lines(count: usize, expectation: &str) -> Validator {
    let expectation = expectation.to_string();
    Box::new(move |envelope| {
        let lines = count_lines(&first_arg(envelope));
        if lines >= count {
            ValidationResult::pass()
        } else {
            ValidationResult::fail(format!(
                "The argument has {lines} non-empty line(s), but the task expects at least {count}. Expected: {expectation}."
            ))
        }
    })
}

/// The first arg contains at least one number.
pub fn contains_number(expectation: &str) -> Validator {
    let expectation = expectation.to_string();
    Box::new(move |envelope| {
        if first_arg(envelope).chars().any(|c| c.is_ascii_digit()) {
            ValidationResult::pass()
        } else {
            ValidationResult::fail(format!(
                "The argument does not contain any number. Expected: {expectation}."
            ))
        }
    })
}

/// The first arg matches the pattern (case-insensitive).
pub fn matches(pattern: &str, expectation: &str) -> Validator {
    let regex = RegexBuilder::new(pattern)
        .case_insensitive(true)
        .build()
        .unwrap_or_else(|e| panic!("invalid validation pattern {pattern:?}: {e}"));
    let expectation = expectation.to_string();
    Box::new(move |envelope| {
        if regex.is_match(&first_arg(envelope)) {
            ValidationResult::pass()
        } else {
            ValidationResult::fail(format!(
                "The argument does not match the expected format. Expected: {expectation}."
            ))
        }
    })
}

/// Composition: every predicate must pass; the first one that fails supplies the reason.
pub fn all_of(validators: Vec<Validator>) -> Validator {
    Box::new(move |envelope| {
        for validator in &validators {
            let result = validator(envelope);
            if !result.ok {
                return result;
            }
        }
        ValidationResult::pass()
    })
}

fn first_arg(envelope: &Envelope) -> String {
    envelope
        .args
        .first()
        .map(|s| s.trim().to_string())
        .unwrap_or_default()
}

// Artifacts travel as a single-line JSON string with literal `\n` (see the flows'
// "Compact" note) — counts both real and escaped line breaks.
fn count_lines(value: &str) -> usize {
    value
        .split("\\n")
        .flat_map(|part| part.split('\n'))
        .filter(|line| !line.trim().is_empty())
        .count()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn min_lines_conta_quebras_literais_e_escapadas() {
        let validator = min_lines(2, "list of stories");

        // Artifacts travel as a single-line string with literal \n ("Compact" note).
        let escaped = Envelope::new("tool", "acceptance", vec![r"1. a\n2. b".to_string()]);
        let real = Envelope::new("tool", "acceptance", vec!["1. a\n2. b".to_string()]);
        let single = Envelope::new("tool", "acceptance", vec!["1. a".to_string()]);

        assert!(validator(&escaped).ok);
        assert!(validator(&real).ok);
        assert!(!validator(&single).ok);
    }

    #[test]
    fn contains_number_exige_ao_menos_um_digito() {
        let validator = contains_number("estimates");

        assert!(
            validator(&Envelope::new(
                "tool",
                "risks",
                vec!["5 points".to_string()]
            ))
            .ok
        );
        assert!(
            !validator(&Envelope::new(
                "tool",
                "risks",
                vec!["no points".to_string()]
            ))
            .ok
        );
    }

    #[test]
    fn matches_casa_sem_diferenciar_maiusculas() {
        let validator = matches("READY|NOT READY", "DoR verdict");

        assert!(
            validator(&Envelope::new(
                "tool",
                "finalize",
                vec!["Verdict: ready with caveat".to_string()]
            ))
            .ok
        );
        assert!(
            !validator(&Envelope::new(
                "tool",
                "finalize",
                vec!["approved".to_string()]
            ))
            .ok
        );
    }

    #[test]
    fn matches_com_padrao_ancorado_rejeita_conteudo_que_apenas_contem_o_prefixo() {
        let validator = matches(r"^(PASS\b|FAIL\b)", "verdict");

        assert!(
            validator(&Envelope::new(
                "command",
                "verify",
                vec!["PASS: tests green".to_string()]
            ))
            .ok
        );
        assert!(
            validator(&Envelope::new(
                "command",
                "verify",
                vec!["FAIL: tests red".to_string()]
            ))
            .ok
        );
        assert!(
            !validator(&Envelope::new(
                "command",
                "verify",
                vec!["ran the tests and got PASS".to_string()]
            ))
            .ok
        );
    }

    #[test]
    fn all_of_falha_na_primeira_razao() {
        let validator = all_of(vec![
            not_empty("estimates"),
            contains_number("estimates with points"),
        ]);

        let result = validator(&Envelope::new(
            "tool",
            "risks",
            vec!["no numbers".to_string()],
        ));

        assert!(!result.ok);
        assert!(result.reason.contains("number"));
    }
}
