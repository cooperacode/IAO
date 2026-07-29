//! Typed harness errors.

/// A step's execution timeout was exceeded (see `harness_config::timeout_ms`). Raised and
/// caught inside `task_registry`: becomes a diagnostic on stderr + `"stop"` on stdout — the
/// same graceful-shutdown contract as the other guards (step ceiling and cost ceiling).
#[derive(Debug, thiserror::Error)]
#[error("task execution exceeded the {timeout_ms}ms timeout; stopping.")]
pub struct HarnessTimeoutError {
    pub timeout_ms: i32,
}
