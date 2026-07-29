//! Reusable entry point for a flow. A new domain only needs to define its tasks and call
//! `run` — all the orchestration (dispatch, guards, transport) lives here.

use std::collections::HashMap;

use crate::envelope_validation::Validator;
use crate::task_registry::{self, Action};
use crate::{state_store, trace};

pub fn run(
    args: &[String],
    tasks: &HashMap<String, Action>,
    trace_snapshot_path: &str,
    state_snapshot_path: &str,
    validators: Option<&HashMap<String, Validator>>,
    max_steps: Option<i32>,
    should_reset_on_start: Option<&dyn Fn() -> bool>,
) -> i32 {
    let result = task_registry::dispatch(args, tasks, validators, max_steps, should_reset_on_start);

    // Run complete: freeze the trajectory AND final state as evidence for later
    // evaluation, before a subsequent flow resets the live trace and state. Each flow
    // publishes to ITS OWN path (refinement to last-run.*, evaluation to
    // last-evaluation.*), so evaluation never overwrites what it itself consumes.
    if result == "stop" {
        trace::snapshot(trace_snapshot_path);
        state_store::snapshot(state_snapshot_path);
    }

    // The only point that writes to stdout — the harness's transport channel.
    println!("{result}");
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::lock_cwd;
    use std::sync::Arc;

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

    fn finalize_task() -> HashMap<String, Action> {
        let mut map: HashMap<String, Action> = HashMap::new();
        map.insert("finalize".to_string(), Arc::new(|_| "stop".to_string()));
        map
    }

    #[test]
    fn run_ao_concluir_congela_trajetoria_e_estado_no_caminho_do_flow() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        state_store::set("description", "x");

        run(
            &[r#"{"type":"command","value":"finalize"}"#.to_string()],
            &finalize_task(),
            trace::LAST_RUN_PATH,
            state_store::LAST_RUN_STATE_PATH,
            None,
            None,
            None,
        );

        assert!(std::path::Path::new(trace::LAST_RUN_PATH).exists());
        assert!(std::path::Path::new(state_store::LAST_RUN_STATE_PATH).exists());
        assert_eq!(
            state_store::load_from(state_store::LAST_RUN_STATE_PATH)
                .data
                .get("description"),
            Some(&"x".to_string())
        );
    }

    #[test]
    fn run_avaliacao_nao_sobrescreve_a_evidencia_do_refinamento() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // 1) Refinement completes → last-run.* holds the refinement's evidence.
        state_store::set("description", "refinement");
        run(
            &[r#"{"type":"command","value":"finalize"}"#.to_string()],
            &finalize_task(),
            trace::LAST_RUN_PATH,
            state_store::LAST_RUN_STATE_PATH,
            None,
            None,
            None,
        );
        let refinement_trace = std::fs::read_to_string(trace::LAST_RUN_PATH).unwrap();

        // 2) Evaluation completes using ITS OWN paths (last-evaluation.*).
        let mut start_task: HashMap<String, Action> = HashMap::new();
        start_task.insert("start".to_string(), Arc::new(|_| "stop".to_string()));
        run(
            &[r#"{"type":"text","value":"start"}"#.to_string()],
            &start_task,
            trace::LAST_EVALUATION_PATH,
            state_store::LAST_EVALUATION_STATE_PATH,
            None,
            None,
            None,
        );

        // Evaluation recorded its own evidence...
        assert!(std::path::Path::new(trace::LAST_EVALUATION_PATH).exists());
        // ...and did NOT touch the refinement's.
        assert_eq!(
            std::fs::read_to_string(trace::LAST_RUN_PATH).unwrap(),
            refinement_trace
        );
        assert_eq!(
            state_store::load_from(state_store::LAST_RUN_STATE_PATH)
                .data
                .get("description"),
            Some(&"refinement".to_string())
        );
    }
}
