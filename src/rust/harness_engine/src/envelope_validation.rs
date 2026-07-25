//! Predicados determinísticos e baratos para validar se o valor devolvido pelo driver
//! atende à expectativa da task — ANTES de persisti-lo e seguir o flow. Falhou → o
//! `TaskRegistry` devolve um erro corretivo tipado e o driver reenvia (loop corretivo,
//! não término mudo).
//!
//! Validação semântica profunda continua sendo trabalho do juiz-LLM na avaliação; aqui
//! mora só o que é checável em código, com zero token.

use regex::RegexBuilder;

use crate::envelope::Envelope;

/// Resultado de uma validação contextual: ok, ou a razão da recusa (para o erro corretivo).
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

/// O primeiro arg existe e não é vazio/whitespace.
pub fn not_empty(expectation: &str) -> Validator {
    let expectation = expectation.to_string();
    Box::new(move |envelope| {
        if !first_arg(envelope).is_empty() {
            ValidationResult::pass()
        } else {
            ValidationResult::fail(format!(
                "O argumento esperado veio vazio. Esperado: {expectation}."
            ))
        }
    })
}

/// O primeiro arg tem ao menos `count` linhas não vazias (contando `\n` literais).
pub fn min_lines(count: usize, expectation: &str) -> Validator {
    let expectation = expectation.to_string();
    Box::new(move |envelope| {
        let lines = count_lines(&first_arg(envelope));
        if lines >= count {
            ValidationResult::pass()
        } else {
            ValidationResult::fail(format!(
                "O argumento tem {lines} linha(s) úteis, mas a task espera ao menos {count}. Esperado: {expectation}."
            ))
        }
    })
}

/// O primeiro arg contém ao menos um número.
pub fn contains_number(expectation: &str) -> Validator {
    let expectation = expectation.to_string();
    Box::new(move |envelope| {
        if first_arg(envelope).chars().any(|c| c.is_ascii_digit()) {
            ValidationResult::pass()
        } else {
            ValidationResult::fail(format!(
                "O argumento não contém nenhum número. Esperado: {expectation}."
            ))
        }
    })
}

/// O primeiro arg casa com o padrão (case-insensitive).
pub fn matches(pattern: &str, expectation: &str) -> Validator {
    let regex = RegexBuilder::new(pattern)
        .case_insensitive(true)
        .build()
        .unwrap_or_else(|e| panic!("padrão de validação inválido {pattern:?}: {e}"));
    let expectation = expectation.to_string();
    Box::new(move |envelope| {
        if regex.is_match(&first_arg(envelope)) {
            ValidationResult::pass()
        } else {
            ValidationResult::fail(format!(
                "O argumento não atende ao formato esperado. Esperado: {expectation}."
            ))
        }
    })
}

/// Composição: todos os predicados precisam passar; o primeiro que falhar dá a razão.
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

// Artefatos trafegam como string JSON de uma linha com `\n` literais (ver o aviso
// "Compact" dos flows) — conta tanto quebras reais quanto escapadas.
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
        let validator = min_lines(2, "lista de histórias");

        // Artefatos trafegam como string de uma linha com \n literais (aviso "Compact").
        let escaped = Envelope::new("tool", "acceptance", vec![r"1. a\n2. b".to_string()]);
        let real = Envelope::new("tool", "acceptance", vec!["1. a\n2. b".to_string()]);
        let single = Envelope::new("tool", "acceptance", vec!["1. a".to_string()]);

        assert!(validator(&escaped).ok);
        assert!(validator(&real).ok);
        assert!(!validator(&single).ok);
    }

    #[test]
    fn contains_number_exige_ao_menos_um_digito() {
        let validator = contains_number("estimativas");

        assert!(
            validator(&Envelope::new(
                "tool",
                "risks",
                vec!["5 pontos".to_string()]
            ))
            .ok
        );
        assert!(
            !validator(&Envelope::new(
                "tool",
                "risks",
                vec!["sem pontos".to_string()]
            ))
            .ok
        );
    }

    #[test]
    fn matches_casa_sem_diferenciar_maiusculas() {
        let validator = matches("READY|NOT READY", "veredito do DoR");

        assert!(
            validator(&Envelope::new(
                "tool",
                "finalize",
                vec!["Veredito: ready com ressalva".to_string()]
            ))
            .ok
        );
        assert!(
            !validator(&Envelope::new(
                "tool",
                "finalize",
                vec!["aprovado".to_string()]
            ))
            .ok
        );
    }

    #[test]
    fn matches_com_padrao_ancorado_rejeita_conteudo_que_apenas_contem_o_prefixo() {
        let validator = matches(r"^(PASS\b|FAIL\b)", "veredito");

        assert!(
            validator(&Envelope::new(
                "command",
                "verify",
                vec!["PASS: testes verdes".to_string()]
            ))
            .ok
        );
        assert!(
            validator(&Envelope::new(
                "command",
                "verify",
                vec!["FAIL: testes vermelhos".to_string()]
            ))
            .ok
        );
        assert!(
            !validator(&Envelope::new(
                "command",
                "verify",
                vec!["rodei os testes e deu PASS".to_string()]
            ))
            .ok
        );
    }

    #[test]
    fn all_of_falha_na_primeira_razao() {
        let validator = all_of(vec![
            not_empty("estimativas"),
            contains_number("estimativas com pontos"),
        ]);

        let result = validator(&Envelope::new(
            "tool",
            "risks",
            vec!["sem numeros".to_string()],
        ));

        assert!(!result.ok);
        assert!(result.reason.contains("número"));
    }
}
