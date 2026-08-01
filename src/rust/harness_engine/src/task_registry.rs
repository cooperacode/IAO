//! Domain-agnostic dispatch: envelope parsing, iteration guard, and typed error.

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::mpsc;
use std::thread;
use std::time::Duration;

use crate::envelope::Envelope;
use crate::envelope_validation::Validator;
use crate::errors::HarnessTimeoutError;
use crate::trace::trace_outcome;
use crate::{context_policy, harness_config, inbox, state_store, trace};

/// `Arc` (not `Box`) on purpose: the time guard needs to move the action onto a detached,
/// abandonable thread (see `run_with_timeout`) without depending on the caller's command
/// registry's lifetime — the Rust equivalent of the shared object that `Task.Run` (.NET)
/// and the thread (Python) capture for free via GC/refcounting.
pub type Action = Arc<dyn Fn(Option<&Envelope>) -> String + Send + Sync>;

/// Step ceiling: prevents an infinite loop that would burn tokens indefinitely. Value
/// comes from harness.json (or the default) — see `harness_config`.
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
    // Argv present → classic transport (backward compatible). Empty argv → reads the
    // envelope from the file-based inbox, the transport that eliminates the shell quoting
    // hang (see inbox).
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

    // Only consumes the inbox when the parse succeeded — a broken JSON must produce the
    // corrective ERROR and remain available for inspection, not vanish silently.
    if from_inbox && envelope.is_some() {
        inbox::consume();
    }

    // Budget stops remain terminal. A timeout is recoverable only through an explicit
    // `start`: the timed-out worker was abandoned with the previous process, and the
    // driver is deliberately asking the flow to resume or restart.
    if let Some(terminal) = state_store::terminal_reason() {
        if terminal == "timeout"
            && envelope
                .as_ref()
                .is_some_and(|envelope| envelope.value == "start")
        {
            state_store::clear_terminal();
        } else {
            eprintln!("[harness] run already stopped ({terminal}); refusing another turn.");
            return "stop".to_string();
        }
    }

    if let Some(env) = &envelope {
        if env.value == "start" {
            // A new workflow starts from scratch — state and trace are truncated
            // together. But a "start" also arrives when a fresh session (e.g. a
            // per-feature hard reset in Development) reopens an in-progress run — in that
            // case it's a RESUME, not a start, and truncating here would wipe the
            // trace/step accumulated by previous features. The flow decides via
            // should_reset_on_start; with no predicate, the default is to always reset
            // (backward compatible with single-shot flows).
            let should_reset = should_reset_on_start.map(|f| f()).unwrap_or(true);
            if should_reset {
                state_store::reset();
                trace::reset();
            }

            // Driver context (e.g. {"driver":"claude code"}) is born here and survives in
            // state_store — prompt_formatter reinjects it into every output until the
            // next "start". Independent of the reset above: even on a resume, the current
            // driver must prevail.
            if let Some(context) = &env.context {
                if !context.is_empty() {
                    state_store::set_context(context.clone());
                }
            }
        }
    }

    let observed_context_usage = if let Some(env) = &envelope {
        if let Some(usage) = env.context_usage.as_ref() {
            Some(usage.clone())
        } else {
            context_policy::ContextUsage::from_environment()
        }
    } else {
        context_policy::ContextUsage::from_environment()
    };
    context_policy::observe(observed_context_usage.as_ref());

    // Iteration guard — hard stop under the team's token budget constraint.
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

    // One line per loop iteration: feeds telemetry and the trajectory evaluator. Label is
    // read again (not from the load() snapshot above) because the action itself may have
    // just set it (e.g. pick() choosing this step's feature).
    let label = state_store::get(state_store::TRACE_LABEL_KEY).unwrap_or_default();
    trace::append_with_context(
        step,
        &command,
        outcome,
        result.len() as i32,
        &label,
        observed_context_usage.as_ref(),
    );

    // The cost of the instruction just emitted is only known here — it feeds into the
    // accumulator the next turn's guard will check.
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
    // Effective step ceiling: the per-invocation override (e.g. a long-running flow like
    // Development, which needs more headroom) takes precedence over harness.json's
    // global. Without an override, the config's value applies.
    let effective_max_steps = max_steps.unwrap_or_else(default_max_steps);
    if step > effective_max_steps {
        eprintln!("[harness] step limit of {effective_max_steps} reached; stopping.");
        state_store::mark_terminal("budget");
        return ("stop".to_string(), trace_outcome::BUDGET);
    }

    // Cost ceiling, a second guard alongside the step one. Emitted instruction chars are
    // the only measure: it's what the engine attests to on its own. Real tokens live in
    // the caller's billing metadata — an LLM driver has no way to honestly report them.
    let config = harness_config::current();
    if config.max_instruction_chars > 0 && cost_chars > config.max_instruction_chars {
        eprintln!(
            "[harness] instruction char limit of {} reached ({cost_chars}); stopping.",
            config.max_instruction_chars
        );
        state_store::mark_terminal("budget");
        return ("stop".to_string(), trace_outcome::BUDGET);
    }

    // Typed error instead of a silent "stop": the model gets the cause and can resend the
    // correct command (corrective loop, not a silent stall).
    let envelope = match envelope {
        None => {
            return (
                error_instruction("Could not parse the received JSON.", actions),
                trace_outcome::ERROR,
            );
        }
        Some(e) => e,
    };

    let action = match actions.get(&envelope.value) {
        None => {
            return (
                error_instruction(
                    &format!("The command '{}' does not exist.", envelope.value),
                    actions,
                ),
                trace_outcome::ERROR,
            );
        }
        Some(a) => a,
    };

    // Contextual validation: the command exists, but does the VALUE meet the task's
    // expectation? On failure → the same corrective-error path as the cases above; the
    // driver fixes it and resends.
    if let Some(validators) = validators {
        if let Some(validator) = validators.get(&envelope.value) {
            let rejected = validator(envelope);
            if !rejected.ok {
                return (
                    error_instruction(
                        &format!(
                            "The command '{}' was rejected: {} Fix the 'args' content and resend the same command.",
                            envelope.value, rejected.reason
                        ),
                        actions,
                    ),
                    trace_outcome::ERROR,
                );
            }
        }
    }

    // Time guard: a stuck task (infinite loop in domain logic) would hang the process
    // indefinitely. `run_with_timeout` enforces the per-step ceiling; the overrun becomes
    // a typed error, caught here, and follows the same graceful path as the budget cutoff:
    // diagnostic on stderr + "stop" on stdout (the channel read by the IDE client).
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
            state_store::mark_terminal("timeout");
            ("stop".to_string(), trace_outcome::TIMEOUT)
        }
    }
}

// The task is a synchronous, OPAQUE closure — it doesn't cooperate with cancellation.
// Rust cannot safely abort stuck synchronous code, so the only real preemptive timeout is
// to run it on another thread and ABANDON whatever hangs. `thread::spawn` (not
// `thread::scope`) because the process kills all threads on exiting `main`, even a spawned
// one still alive — the same model as .NET's `Task.Run` (threadpool) and Python's
// `daemon=True` thread; `thread::scope` would block until the stuck thread finishes,
// nullifying the timeout.
fn run_with_timeout(
    action: &Action,
    envelope: Option<&Envelope>,
    timeout_ms: i32,
) -> Result<String, HarnessTimeoutError> {
    if timeout_ms <= 0 {
        return Ok(action(envelope)); // guard disabled — no thread overhead
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
    // Sorted for determinism: `HashMap` doesn't guarantee stable iteration order across
    // runs (unlike Python's `dict`), and the message is more useful already sorted.
    let mut keys: Vec<&str> = actions.keys().map(|k| k.as_str()).collect();
    keys.sort_unstable();
    let valid = keys.join(", ");

    format!(
        "HARNESS PROTOCOL ERROR: {reason} Valid commands: {valid}. \
Review the 'value' field in your JSON response (reply with the JSON only, no code fences \
or commentary) and resend the command."
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
            &arg(r#"{"type":"text","value":"type"}"#),
            &tasks(),
            None,
            None,
            None,
        );

        assert!(result.starts_with("HARNESS PROTOCOL ERROR"));
        assert_ne!(result, "stop");
        assert!(result.contains("'type'"));
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

        assert!(result.starts_with("HARNESS PROTOCOL ERROR"));
        assert_ne!(result, "stop");
    }

    #[test]
    fn dispatch_sem_argumento_retorna_erro_e_nao_stop() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = dispatch(&[], &tasks(), None, None, None);

        assert!(result.starts_with("HARNESS PROTOCOL ERROR"));
        assert_ne!(result, "stop");
    }

    #[test]
    fn dispatch_mensagem_de_erro_lista_os_comandos_validos() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = dispatch(
            &arg(r#"{"type":"text","value":"nonexistent"}"#),
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

        // start resets and then counts itself as step 1.
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

        assert_eq!(state_store::load().step, 4); // 3 previous + "start" itself, no reset
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

        assert_eq!(state_store::load().step, 1); // backward compatible: no predicate, always resets
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

        // The next step exceeds the ceiling and is cut off.
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

        // SAFETY: serialized by `lock_cwd()`.
        unsafe { std::env::set_var("HARNESS_TIMEOUT_MS", "50") };

        let mut slow: HashMap<String, Action> = HashMap::new();
        slow.insert(
            "start".to_string(),
            Arc::new(|_| "PROMPT_START".to_string()),
        );
        slow.insert(
            "slow".to_string(),
            Arc::new(|_| {
                thread::sleep(Duration::from_millis(500));
                "never arrives".to_string()
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

        // SAFETY: see above.
        unsafe { std::env::remove_var("HARNESS_TIMEOUT_MS") };
        harness_config::reload();

        assert_eq!(result, "stop");
        // Non-start commands remain terminal even if the workspace config is changed.
        std::fs::write("harness.json", r#"{"timeoutMs":0}"#).unwrap();
        harness_config::reload();
        let later = dispatch(
            &arg(r#"{"type":"command","value":"classify","args":["x"]}"#),
            &tasks(),
            None,
            None,
            None,
        );
        assert_eq!(later, "stop");

        // An explicit start clears only the recoverable timeout latch.
        let resumed = dispatch(
            &arg(r#"{"type":"text","value":"start"}"#),
            &slow,
            None,
            None,
            None,
        );
        assert_eq!(resumed, "PROMPT_START");
        assert!(state_store::terminal_reason().is_none());
    }
}
