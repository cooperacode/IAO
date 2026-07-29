//! Every harness invocation is a new process with no memory. This store persists the
//! accumulated state (step counter + domain data) to file, so the envelope carried by
//! the model stays minimal — token savings: the model passes a key, not the whole
//! state, on every loop iteration.

use std::collections::HashMap;

use crate::harness_state::HarnessState;

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/state.json";

/// Final state frozen from the last completed run. Exists for the same reason as
/// `trace::LAST_RUN_PATH`: any flow's `start` resets the live `state.json`, so the
/// evaluation (which checks completeness) needs to read the domain keys from a stable
/// snapshot, not from the file its own `start` just zeroed out.
pub const LAST_RUN_STATE_PATH: &str = ".harness/last-run.state.json";

/// Final state frozen from the last evaluation run — its own path, doesn't overwrite the
/// refinement's.
pub const LAST_EVALUATION_STATE_PATH: &str = ".harness/last-evaluation.state.json";

/// Conventional key in `HarnessState::data` for the label that `task_registry` propagates
/// to `trace` on every step (see `trace::TraceEntry::label`). Generic on purpose: the
/// engine doesn't know what a "feature" is — it only re-reads this key if the flow has
/// set it (e.g. `flows_development::tasks::pick`).
pub const TRACE_LABEL_KEY: &str = "trace_label";

pub fn load() -> HarnessState {
    load_from(FILE_PATH)
}

/// Loads a state from an arbitrary path (e.g. the evidence of a golden set case).
pub fn load_from(path: &str) -> HarnessState {
    let p = std::path::Path::new(path);
    if p.exists() {
        let loaded = std::fs::read_to_string(p)
            .map_err(|e| e.to_string())
            .and_then(|json| {
                serde_json::from_str::<HarnessState>(&json).map_err(|e| e.to_string())
            });

        match loaded {
            Ok(state) => return state,
            Err(e) => eprintln!("[StateStore] failed to load: {e}"),
        }
    }

    HarnessState::new(0, HashMap::new())
}

pub fn save(state: &HarnessState) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[StateStore] failed to save: {e}");
        return;
    }
    match serde_json::to_string(state) {
        Ok(json) => {
            if let Err(e) =
                crate::atomic_io::write_atomic(std::path::Path::new(FILE_PATH), &json)
            {
                eprintln!("[StateStore] failed to save: {e}");
            }
        }
        Err(e) => eprintln!("[StateStore] failed to save: {e}"),
    }
}

pub fn reset() {
    save(&HarnessState::new(0, HashMap::new()));
}

/// Freezes the live `state.json` at the destination — the evidence of the completed run's completeness.
pub fn snapshot(destination: &str) {
    if std::path::Path::new(FILE_PATH).exists() {
        if let Err(e) = std::fs::create_dir_all(DIR) {
            eprintln!("[StateStore] failed to freeze: {e}");
            return;
        }
        if let Err(e) = std::fs::copy(FILE_PATH, destination) {
            eprintln!("[StateStore] failed to freeze: {e}");
        }
    }
}

pub fn increment() -> i32 {
    let state = load();
    let next = state.step + 1;
    save(&HarnessState {
        step: next,
        ..state
    });
    next
}

/// Adds the turn's cost to the run's accumulator and returns the total — the input to
/// the cost ceiling in `task_registry`. Emitted instruction chars are the only measure:
/// it's what the engine can attest to on its own, without relying on driver self-reporting.
pub fn add_cost(chars: i32) -> i32 {
    let state = load();
    let next_cost = state.cost_chars + chars;
    let next = HarnessState {
        cost_chars: next_cost,
        ..state
    };
    save(&next);
    next.cost_chars
}

pub fn set(key: &str, value: &str) {
    let mut state = load();
    state.data.insert(key.to_string(), value.to_string());
    save(&state);
}

pub fn get(key: &str) -> Option<String> {
    load().data.get(key).cloned()
}

/// Persists the driver context captured on `start` (see `task_registry`).
pub fn set_context(context: HashMap<String, String>) {
    let state = load();
    save(&HarnessState {
        context: Some(context),
        ..state
    });
}

/// Persisted driver context, for `prompt_formatter` to reinject into every output.
pub fn get_context() -> Option<HashMap<String, String>> {
    load().context
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

    #[test]
    fn set_e_get_persistem_entre_chamadas() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        set("description", "Login with Google");

        assert_eq!(get("description"), Some("Login with Google".to_string()));
    }

    #[test]
    fn get_chave_inexistente_retorna_none() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert_eq!(get("does-not-exist"), None);
    }

    #[test]
    fn set_sobrescreve_a_chave_existente() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        set("type", "Bug");
        set("type", "Epic");

        assert_eq!(get("type"), Some("Epic".to_string()));
    }

    #[test]
    fn increment_avanca_o_contador() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert_eq!(increment(), 1);
        assert_eq!(increment(), 2);
        assert_eq!(increment(), 3);
        assert_eq!(load().step, 3);
    }

    #[test]
    fn increment_preserva_os_dados_acumulados() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        set("description", "x");
        increment();

        assert_eq!(get("description"), Some("x".to_string()));
    }

    #[test]
    fn reset_limpa_contador_e_dados() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        set("description", "x");
        increment();

        reset();

        assert_eq!(load().step, 0);
        assert_eq!(get("description"), None);
    }

    #[test]
    fn set_context_e_get_context_persistem_entre_chamadas() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        set_context(HashMap::from([(
            "driver".to_string(),
            "claude code".to_string(),
        )]));

        assert_eq!(get_context().unwrap().get("driver").unwrap(), "claude code");
    }

    #[test]
    fn get_context_sem_contexto_definido_retorna_none() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert_eq!(get_context(), None);
    }

    #[test]
    fn reset_limpa_o_contexto() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        set_context(HashMap::from([(
            "driver".to_string(),
            "claude code".to_string(),
        )]));

        reset();

        assert_eq!(get_context(), None);
    }
}
