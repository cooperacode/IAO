pub mod artifact_store;
pub mod artifact_template;
pub mod batch_evaluator;
pub mod docs_reader;
pub mod envelope;
pub mod envelope_validation;
pub mod errors;
pub mod evaluators;
pub mod feature_store;
pub mod golden_case_store;
pub mod harness_config;
pub mod harness_host;
pub mod harness_state;
pub mod inbox;
pub mod path_resolver;
pub mod prompt_formatter;
pub mod run_config_store;
pub mod score_store;
pub mod state_store;
pub mod task_registry;
pub mod trace;

pub use envelope::{Envelope, envelope_type};
pub use envelope_validation::{ValidationResult, Validator};
pub use errors::HarnessTimeoutError;
pub use harness_config::HarnessConfig;
pub use harness_state::HarnessState;
pub use run_config_store::RunConfig;
pub use task_registry::Action;
pub use trace::TraceEntry;

// `current_dir` (e no futuro env vars usadas por outros módulos) são globais ao processo:
// testes de módulos diferentes que os mutam precisam compartilhar UM único lock, senão
// corrida entre `cargo test` threads = flakiness (equivalente ao
// `DisableTestParallelization` do lado .NET).
#[cfg(test)]
pub(crate) mod test_support {
    use std::sync::{Mutex, MutexGuard};

    pub static CWD_GUARD: Mutex<()> = Mutex::new(());

    pub fn lock_cwd() -> MutexGuard<'static, ()> {
        CWD_GUARD
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}
