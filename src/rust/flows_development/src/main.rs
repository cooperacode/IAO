//! "Long-running agent" pattern: initializer + loop of fresh sessions, one feature at a
//! time. No orchestration here — dispatch, guards, and transport live in
//! `harness_engine`.
//!
//!   start → plan → [implement → verify(auto-handoff)]*

mod handoff;
mod prompts;
mod tasks;
mod verify;

use std::collections::HashMap;
use std::sync::Arc;

use harness_engine::envelope_validation;
use harness_engine::feature_store;
use harness_engine::harness_host;
use harness_engine::task_registry::Action;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();

    let mut tasks: HashMap<String, Action> = HashMap::new();
    tasks.insert("start".to_string(), Arc::new(|_| tasks::start()));
    tasks.insert("plan".to_string(), Arc::new(tasks::plan));
    tasks.insert("bearings".to_string(), Arc::new(tasks::bearings));
    tasks.insert("smoke".to_string(), Arc::new(tasks::smoke));
    tasks.insert("pick".to_string(), Arc::new(tasks::pick));
    tasks.insert("implement".to_string(), Arc::new(tasks::implement));
    tasks.insert("verify".to_string(), Arc::new(tasks::verify));
    tasks.insert("handoff".to_string(), Arc::new(tasks::handoff_task));

    // Contextual expectation per command; a rejection becomes a corrective error (the
    // driver fixes and resends). `pick` has no validator — it doesn't carry a driver
    // artifact (the selection is the harness's).
    let mut validators: HashMap<String, envelope_validation::Validator> = HashMap::new();
    validators.insert(
        "plan".to_string(),
        envelope_validation::not_empty("the JSON array of features [{id,title,priority}]"),
    );

    // Own snapshots: if this flow shares `.harness/` with other flows (same workspace), it
    // must NOT overwrite the last-run.* another flow consumes. Freezes at its own path.
    // max_steps: override of the global ceiling — this flow is long-running and needs
    // slack for the loop.
    // should_reset_on_start: a "start" also arrives on the per-feature hard reset (a fresh
    // session reopening a run in progress) — it's only a genuinely new run when there's no
    // pending feature.
    let should_reset_on_start: &dyn Fn() -> bool = &|| feature_store::pending_count() == 0;

    let code = harness_host::run(
        &args,
        &tasks,
        ".harness/last-development.trace.jsonl",
        ".harness/last-development.state.json",
        Some(&validators),
        Some(tasks::STEP_BUDGET),
        Some(should_reset_on_start),
    );

    std::process::exit(code);
}
