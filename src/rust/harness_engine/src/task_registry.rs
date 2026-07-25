//! Dispatch domain-agnostic: parse do envelope, guarda de iteração e erro tipado.

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::mpsc;
use std::thread;
use std::time::Duration;

use crate::envelope::Envelope;
use crate::envelope_validation::Validator;
use crate::errors::HarnessTimeoutError;
use crate::trace::trace_outcome;
use crate::{harness_config, inbox, state_store, trace};

/// `Arc` (não `Box`) de propósito: a guarda de tempo precisa mover a action para uma
/// thread solta e abandonável (ver `run_with_timeout`) sem depender do tempo de vida do
/// registro de comandos do chamador — o equivalente Rust ao objeto compartilhado que
/// `Task.Run` (.NET) e a thread (Python) capturam de graça via GC/refcounting.
pub type Action = Arc<dyn Fn(Option<&Envelope>) -> String + Send + Sync>;

/// Teto de passos: impede loop infinito que queimaria tokens indefinidamente. Valor vem
/// do harness.json (ou do default) — ver `harness_config`.
pub fn default_max_steps() -> i32 {
    harness_config::current().max_steps
}

pub fn dispatch(
    args: &[String],
    actions: &HashMap<String, Action>,
    validators: Option<&HashMap<String, Validator>>,
    max_steps: Option<i32>,
    should_reset_on_start: Option<&dyn Fn() -> bool>,
) -> String {
    // Argv presente → transporte clássico (retrocompatível). Argv vazio → lê o envelope
    // da inbox em arquivo, o transporte que elimina o hang de aspas do shell (ver inbox).
    let from_inbox = args.is_empty();
    let arg0 = if !args.is_empty() {
        args[0].clone()
    } else {
        inbox::read()
    };

    let envelope = if arg0.trim().is_empty() {
        None
    } else {
        Envelope::parse(&arg0)
    };

    // Só consome a inbox quando o parse deu certo — um JSON quebrado deve gerar o ERRO
    // corretivo e permanecer disponível para inspeção, não sumir silenciosamente.
    if from_inbox && envelope.is_some() {
        inbox::consume();
    }

    if let Some(env) = &envelope {
        if env.value == "start" {
            // Novo workflow começa do zero — estado e trace são truncados juntos. Mas um
            // "start" também chega quando uma sessão fresca (ex.: hard reset por feature
            // do Development) reabre um run em andamento — nesse caso é RETOMADA, não
            // início, e truncar aqui apagaria o trace/step acumulados de features
            // anteriores. O flow decide via should_reset_on_start; sem predicado, o
            // padrão é sempre resetar (retrocompatível com flows single-shot).
            let should_reset = should_reset_on_start.map(|f| f()).unwrap_or(true);
            if should_reset {
                state_store::reset();
                trace::reset();
            }

            // Contexto do driver (ex.: {"driver":"claude code"}) nasce aqui e sobrevive
            // no state_store — prompt_formatter o reinjeta em toda saída até o próximo
            // "start". Independe do reset acima: mesmo numa retomada, o driver atual
            // deve prevalecer.
            if let Some(context) = &env.context {
                if !context.is_empty() {
                    state_store::set_context(context.clone());
                }
            }
        }
    }

    // Guarda de iteração — hard stop sob a restrição de tokens do time.
    let step = state_store::increment();

    let cost_chars = state_store::load().cost_chars;
    let command = envelope
        .as_ref()
        .map(|e| e.value.clone())
        .filter(|v| !v.is_empty())
        .unwrap_or_else(|| "(unparsed)".to_string());

    let (result, outcome) = resolve(
        envelope.as_ref(),
        step,
        cost_chars,
        actions,
        validators,
        max_steps,
    );

    // Uma linha por volta do loop: alimenta a telemetria e o evaluator de trajetória.
    trace::append(step, &command, outcome, result.len() as i32);

    // O custo da instrução emitida agora só é conhecido aqui — entra no acumulado que o
    // guard do próximo turno vai checar.
    state_store::add_cost(result.len() as i32);
    result
}

fn resolve(
    envelope: Option<&Envelope>,
    step: i32,
    cost_chars: i32,
    actions: &HashMap<String, Action>,
    validators: Option<&HashMap<String, Validator>>,
    max_steps: Option<i32>,
) -> (String, &'static str) {
    // Teto de passos efetivo: o override por invocação (ex.: um flow long-running como o
    // Development, que precisa de mais folga) tem precedência sobre o global do
    // harness.json. Sem override, vale o do config.
    let effective_max_steps = max_steps.unwrap_or_else(default_max_steps);
    if step > effective_max_steps {
        eprintln!("[harness] limite de {effective_max_steps} passos atingido; encerrando.");
        return ("stop".to_string(), trace_outcome::BUDGET);
    }

    // Teto de custo, segundo guard além do de passos. Chars de instrução emitida são a
    // única medida: é o que a engine atesta sozinha. Token real vive nos metadados de
    // billing do caller — um driver-LLM não tem como reportá-lo honestamente.
    let config = harness_config::current();
    if config.max_instruction_chars > 0 && cost_chars > config.max_instruction_chars {
        eprintln!(
            "[harness] limite de {} chars de instrução atingido ({cost_chars}); encerrando.",
            config.max_instruction_chars
        );
        return ("stop".to_string(), trace_outcome::BUDGET);
    }

    // Erro tipado em vez de "stop" silencioso: o modelo recebe a causa e pode reenviar o
    // comando correto (loop corretivo, não término mudo).
    let envelope = match envelope {
        None => {
            return (
                error_instruction("Não foi possível interpretar o JSON recebido.", actions),
                trace_outcome::ERROR,
            );
        }
        Some(e) => e,
    };

    let action = match actions.get(&envelope.value) {
        None => {
            return (
                error_instruction(
                    &format!("O comando '{}' não existe.", envelope.value),
                    actions,
                ),
                trace_outcome::ERROR,
            );
        }
        Some(a) => a,
    };

    // Validação contextual: o comando existe, mas o VALOR atende à expectativa da task?
    // Falhou → mesmo caminho de erro corretivo dos casos acima; o driver corrige e reenvia.
    if let Some(validators) = validators {
        if let Some(validator) = validators.get(&envelope.value) {
            let rejected = validator(envelope);
            if !rejected.ok {
                return (
                    error_instruction(
                        &format!(
                            "O comando '{}' foi recusado: {} Corrija o conteúdo de 'args' e reenvie o mesmo comando.",
                            envelope.value, rejected.reason
                        ),
                        actions,
                    ),
                    trace_outcome::ERROR,
                );
            }
        }
    }

    // Guarda de tempo: uma task travada (loop infinito na lógica de domínio) prenderia o
    // processo indefinidamente. `run_with_timeout` impõe o teto por passo; o estouro vira
    // erro tipado, capturado aqui, e segue o mesmo caminho gracioso do corte por budget:
    // diagnóstico no stderr + "stop" no stdout (o canal lido pelo cliente IDE).
    match run_with_timeout(action, Some(envelope), config.timeout_ms) {
        Ok(result) => {
            let outcome = if result == "stop" {
                trace_outcome::STOP
            } else {
                trace_outcome::INSTRUCTION
            };
            (result, outcome)
        }
        Err(e) => {
            eprintln!("[harness] {e}");
            ("stop".to_string(), trace_outcome::TIMEOUT)
        }
    }
}

// A task é uma closure síncrona e OPACA — não coopera com cancelamento. Rust não aborta
// código síncrono travado com segurança, então o único timeout preemptivo real é rodá-la
// noutra thread e ABANDONAR o que travar. `thread::spawn` (não `thread::scope`) porque o
// processo termina todas as threads ao sair do `main`, mesmo com uma spawned ainda viva —
// o mesmo modelo do `Task.Run` (threadpool) do .NET e da thread `daemon=True` do Python;
// `thread::scope` bloquearia até a thread travada terminar, anulando o timeout.
fn run_with_timeout(
    action: &Action,
    envelope: Option<&Envelope>,
    timeout_ms: i32,
) -> Result<String, HarnessTimeoutError> {
    if timeout_ms <= 0 {
        return Ok(action(envelope)); // guarda desligada — sem overhead de thread
    }

    let action = Arc::clone(action);
    let envelope_owned = envelope.cloned();

    let (tx, rx) = mpsc::channel();
    thread::spawn(move || {
        let result = action(envelope_owned.as_ref());
        let _ = tx.send(result);
    });

    rx.recv_timeout(Duration::from_millis(timeout_ms as u64))
        .map_err(|_| HarnessTimeoutError { timeout_ms })
}

fn error_instruction(reason: &str, actions: &HashMap<String, Action>) -> String {
    // Ordenado por determinismo: `HashMap` não garante ordem de iteração estável entre
    // execuções (ao contrário do `dict` do Python), e a mensagem é mais útil já ordenada.
    let mut keys: Vec<&str> = actions.keys().map(|k| k.as_str()).collect();
    keys.sort_unstable();
    let valid = keys.join(", ");

    format!(
        "ERRO no protocolo do harness: {reason} Comandos válidos: {valid}. \
Revise o campo 'value' do seu JSON de resposta (responda apenas com o JSON, sem cercas de \
código nem comentários) e reenvie o comando."
    )
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

    fn tasks() -> HashMap<String, Action> {
        let mut map: HashMap<String, Action> = HashMap::new();
        map.insert(
            "start".to_string(),
            Arc::new(|_| "PROMPT_START".to_string()),
        );
        map.insert(
            "classify".to_string(),
            Arc::new(|e: Option<&Envelope>| {
                let arg = e.and_then(|e| e.args.first()).cloned().unwrap_or_default();
                format!("PROMPT_CLASSIFY:{arg}")
            }),
        );
        map.insert("finalize".to_string(), Arc::new(|_| "stop".to_string()));
        map
    }

    fn arg(json: &str) -> Vec<String> {
        vec![json.to_string()]
    }

    #[test]
    fn dispatch_comando_registrado_executa_a_action() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = dispatch(
            &arg(r#"{"type":"text","value":"start"}"#),
            &tasks(),
            None,
            None,
            None,
        );

        assert_eq!(result, "PROMPT_START");
    }

    #[test]
    fn dispatch_repassa_args_para_a_action() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = dispatch(
            &arg(r#"{"type":"tool","value":"classify","args":["Login"]}"#),
            &tasks(),
            None,
            None,
            None,
        );

        assert_eq!(result, "PROMPT_CLASSIFY:Login");
    }

    #[test]
    fn dispatch_finalize_retorna_stop() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = dispatch(
            &arg(r#"{"type":"command","value":"finalize"}"#),
            &tasks(),
            None,
            None,
            None,
        );

        assert_eq!(result, "stop");
    }

    #[test]
    fn dispatch_comando_inexistente_retorna_erro_e_nao_stop() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = dispatch(
            &arg(r#"{"type":"text","value":"tipo"}"#),
            &tasks(),
            None,
            None,
            None,
        );

        assert!(result.starts_with("ERRO"));
        assert_ne!(result, "stop");
        assert!(result.contains("'tipo'"));
    }

    #[test]
    fn dispatch_json_malformado_retorna_erro_e_nao_stop() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = dispatch(
            &arg(r#"{"type":"text","value":"#),
            &tasks(),
            None,
            None,
            None,
        );

        assert!(result.starts_with("ERRO"));
        assert_ne!(result, "stop");
    }

    #[test]
    fn dispatch_sem_argumento_retorna_erro_e_nao_stop() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = dispatch(&[], &tasks(), None, None, None);

        assert!(result.starts_with("ERRO"));
        assert_ne!(result, "stop");
    }

    #[test]
    fn dispatch_mensagem_de_erro_lista_os_comandos_validos() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = dispatch(
            &arg(r#"{"type":"text","value":"inexistente"}"#),
            &tasks(),
            None,
            None,
            None,
        );

        assert!(result.contains("start"));
        assert!(result.contains("classify"));
        assert!(result.contains("finalize"));
    }

    #[test]
    fn dispatch_start_reinicia_o_contador_de_passos() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        for _ in 0..5 {
            dispatch(
                &arg(r#"{"type":"tool","value":"classify","args":["x"]}"#),
                &tasks(),
                None,
                None,
                None,
            );
        }
        assert_eq!(state_store::load().step, 5);

        dispatch(
            &arg(r#"{"type":"text","value":"start"}"#),
            &tasks(),
            None,
            None,
            None,
        );

        // start reseta e então conta a si mesmo como passo 1.
        assert_eq!(state_store::load().step, 1);
    }

    #[test]
    fn dispatch_start_com_should_reset_on_start_falso_nao_trunca_state_nem_trace() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        for _ in 0..3 {
            dispatch(
                &arg(r#"{"type":"tool","value":"classify","args":["x"]}"#),
                &tasks(),
                None,
                None,
                None,
            );
        }
        trace::append(99, "handoff", trace_outcome::INSTRUCTION, 5);

        let never_reset: &dyn Fn() -> bool = &|| false;
        dispatch(
            &arg(r#"{"type":"text","value":"start"}"#),
            &tasks(),
            None,
            None,
            Some(never_reset),
        );

        assert_eq!(state_store::load().step, 4); // 3 anteriores + o próprio "start", sem reset
        assert!(
            trace::load()
                .iter()
                .any(|e| e.step == 99 && e.command == "handoff")
        );
    }

    #[test]
    fn dispatch_start_sem_predicado_mantem_comportamento_padrao_de_sempre_resetar() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        for _ in 0..3 {
            dispatch(
                &arg(r#"{"type":"tool","value":"classify","args":["x"]}"#),
                &tasks(),
                None,
                None,
                None,
            );
        }

        dispatch(
            &arg(r#"{"type":"text","value":"start"}"#),
            &tasks(),
            None,
            None,
            None,
        );

        assert_eq!(state_store::load().step, 1); // retrocompatível: sem predicado, sempre reseta
    }

    #[test]
    fn dispatch_start_com_context_persiste_no_state_store() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        dispatch(
            &arg(r#"{"type":"text","value":"start","context":{"driver":"claude code"}}"#),
            &tasks(),
            None,
            None,
            None,
        );

        assert_eq!(
            state_store::get_context().unwrap().get("driver").unwrap(),
            "claude code"
        );
    }

    #[test]
    fn dispatch_ao_exceder_o_teto_forca_stop() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let max_steps = 3;
        for _ in 0..max_steps {
            let ok = dispatch(
                &arg(r#"{"type":"tool","value":"classify","args":["x"]}"#),
                &tasks(),
                None,
                Some(max_steps),
                None,
            );
            assert_ne!(ok, "stop");
        }

        // O passo seguinte ultrapassa o teto e é cortado.
        let result = dispatch(
            &arg(r#"{"type":"tool","value":"classify","args":["x"]}"#),
            &tasks(),
            None,
            Some(max_steps),
            None,
        );

        assert_eq!(result, "stop");
    }

    #[test]
    fn run_with_timeout_task_lenta_estoura_e_devolve_stop_via_dispatch() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // SAFETY: serializado por `lock_cwd()`.
        unsafe { std::env::set_var("HARNESS_TIMEOUT_MS", "50") };

        let mut slow: HashMap<String, Action> = HashMap::new();
        slow.insert(
            "slow".to_string(),
            Arc::new(|_| {
                thread::sleep(Duration::from_millis(500));
                "nunca chega".to_string()
            }),
        );

        harness_config::reload();
        let result = dispatch(
            &arg(r#"{"type":"command","value":"slow"}"#),
            &slow,
            None,
            None,
            None,
        );

        // SAFETY: ver acima.
        unsafe { std::env::remove_var("HARNESS_TIMEOUT_MS") };
        harness_config::reload();

        assert_eq!(result, "stop");
    }
}
