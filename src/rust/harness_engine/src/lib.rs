pub mod artifact_store;
pub mod artifact_template;
pub mod atomic_io;
pub mod batch_evaluator;
pub mod context_policy;
pub mod docs_reader;
pub mod envelope;
pub mod envelope_validation;
pub mod errors;
pub mod evaluators;
pub mod feature_store;
pub mod git_command;
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
pub use context_policy::ContextUsage;
pub use envelope_validation::{ValidationResult, Validator};
pub use errors::HarnessTimeoutError;
pub use harness_config::HarnessConfig;
pub use harness_state::HarnessState;
pub use run_config_store::RunConfig;
pub use task_registry::Action;
pub use trace::TraceEntry;

// `current_dir` (and, in the future, env vars used by other modules) are process-global:
// tests from different modules that mutate them need to share a SINGLE lock, otherwise a
// race between `cargo test` threads = flakiness (equivalent to the .NET side's
// `DisableTestParallelization`).
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
