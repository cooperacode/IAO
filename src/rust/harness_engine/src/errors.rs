//! Typed harness errors.

/// A step's execution timeout was exceeded (see `harness_config::timeout_ms`). Raised and
/// caught inside `task_registry`: becomes a diagnostic on stderr + `"stop"` on stdout — the
/// same graceful-shutdown contract as the other guards (step ceiling and cost ceiling).
#[derive(Debug, thiserror::Error)]
#[error("task execution exceeded the {timeout_ms}ms timeout; stopping.")]
pub struct HarnessTimeoutError {
    pub timeout_ms: i32,
}

/// A recovered panic from inside a task action — a bug in the harness/flow itself, not a
/// driver protocol error the driver can fix by resending. Recovered via `catch_unwind` in
/// `task_registry::run_protected` so it never crashes the process (nor gets silently
/// mislabeled as a timeout — see the regression test in `task_registry`); surfaced as the
/// `"fault"` trace outcome.
#[derive(Debug, thiserror::Error)]
#[error("unhandled fault in task action: {reason}")]
pub struct HarnessFaultError {
    pub reason: String,
}
