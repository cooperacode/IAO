//! Cada invocação do harness é um processo novo e sem memória. Este store persiste o
//! estado acumulado (contador de passos + dados de domínio) em arquivo, para que o
//! envelope trafegado pelo modelo fique mínimo — economia de tokens: o modelo passa uma
//! chave, não o estado inteiro, a cada volta do loop.

use std::collections::HashMap;

use crate::harness_state::HarnessState;

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/state.json";

/// Estado final congelado do último run concluído. Existe pela mesma razão que
/// `trace::LAST_RUN_PATH`: o `start` de qualquer flow reseta o `state.json` vivo, então a
/// avaliação (que checa completude) precisa ler as chaves de domínio de um snapshot
/// estável, não do arquivo que seu próprio `start` zerou.
pub const LAST_RUN_STATE_PATH: &str = ".harness/last-run.state.json";

/// Estado final congelado do último run de avaliação — caminho próprio, não sobrescreve o
/// do refinamento.
pub const LAST_EVALUATION_STATE_PATH: &str = ".harness/last-evaluation.state.json";

pub fn load() -> HarnessState {
    load_from(FILE_PATH)
}

/// Carrega um estado de um caminho arbitrário (ex.: a evidência de um caso do golden set).
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
            Err(e) => eprintln!("[StateStore] falha ao carregar: {e}"),
        }
    }

    HarnessState::new(0, HashMap::new())
}

pub fn save(state: &HarnessState) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[StateStore] falha ao salvar: {e}");
        return;
    }
    match serde_json::to_string(state) {
        Ok(json) => {
            if let Err(e) =
                crate::atomic_io::write_atomic(std::path::Path::new(FILE_PATH), &json)
            {
                eprintln!("[StateStore] falha ao salvar: {e}");
            }
        }
        Err(e) => eprintln!("[StateStore] falha ao salvar: {e}"),
    }
}

pub fn reset() {
    save(&HarnessState::new(0, HashMap::new()));
}

/// Congela o `state.json` vivo no destino — a evidência de completude do run concluído.
pub fn snapshot(destination: &str) {
    if std::path::Path::new(FILE_PATH).exists() {
        if let Err(e) = std::fs::create_dir_all(DIR) {
            eprintln!("[StateStore] falha ao congelar: {e}");
            return;
        }
        if let Err(e) = std::fs::copy(FILE_PATH, destination) {
            eprintln!("[StateStore] falha ao congelar: {e}");
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

/// Soma o custo do turno ao acumulado do run e devolve o total — insumo do teto de
/// custo em `task_registry`. Chars de instrução emitida são a única medida: é o que a
/// engine consegue atestar sozinha, sem depender de auto-relato do driver.
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

/// Persiste o contexto do driver capturado no `start` (ver `task_registry`).
pub fn set_context(context: HashMap<String, String>) {
    let state = load();
    save(&HarnessState {
        context: Some(context),
        ..state
    });
}

/// Contexto do driver persistido, para `prompt_formatter` reinjetar em toda saída.
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

        set("descricao", "Login com Google");

        assert_eq!(get("descricao"), Some("Login com Google".to_string()));
    }

    #[test]
    fn get_chave_inexistente_retorna_none() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert_eq!(get("nao-existe"), None);
    }

    #[test]
    fn set_sobrescreve_a_chave_existente() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        set("tipo", "Bug");
        set("tipo", "Épico");

        assert_eq!(get("tipo"), Some("Épico".to_string()));
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

        set("descricao", "x");
        increment();

        assert_eq!(get("descricao"), Some("x".to_string()));
    }

    #[test]
    fn reset_limpa_contador_e_dados() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        set("descricao", "x");
        increment();

        reset();

        assert_eq!(load().step, 0);
        assert_eq!(get("descricao"), None);
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
