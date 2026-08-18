mod evaluator;
mod prompts;
mod publisher;
mod renderer;
mod store;
mod tasks;

use harness_engine::Action;
use std::{collections::HashMap, sync::Arc};

fn main() {
    let mut t: HashMap<String, Action> = HashMap::new();
    t.insert("start".into(), Arc::new(|_| tasks::start()));
    t.insert("discover".into(), Arc::new(tasks::discover));
    t.insert("product".into(), Arc::new(tasks::product));
    t.insert("analysis".into(), Arc::new(tasks::analysis));
    t.insert("design".into(), Arc::new(tasks::design));
    t.insert("review".into(), Arc::new(tasks::review));
    t.insert("approve".into(), Arc::new(tasks::approve));

    // A "start" also arrives on a fresh session reopening a run in progress (kill + restart);
    // it's only a genuinely new run when SpecificationStore reports no run in progress —
    // mirrors Program.cs's `shouldResetOnStart: () => SpecificationStore.LoadRun().Status !=
    // "in_progress"` and flows_development/src/main.rs's own predicate wiring.
    let should_reset_on_start: &dyn Fn() -> bool =
        &|| store::run()["status"].as_str().unwrap_or("") != "in_progress";

    harness_engine::harness_host::run(
        &std::env::args().skip(1).collect::<Vec<_>>(),
        &t,
        ".harness/last-specification.trace.jsonl",
        ".harness/last-specification.state.json",
        None,
        Some(tasks::STEP_BUDGET),
        Some(should_reset_on_start),
    );
}
