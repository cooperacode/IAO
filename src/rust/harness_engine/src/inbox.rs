//! File-based input channel — an alternative to argv for the turn's envelope.
//!
//! The single-quoted argument transport (`./run-development.sh '<JSON>'`) has a
//! structural flaw: if the LLM driver forgets the closing quote, the shell enters
//! continuation mode and hangs BEFORE the binary even runs — no engine validation can
//! catch it. The inbox takes the payload out of the shell's quoting syntax: the agent
//! writes the JSON here with its file-write tool (never touching a shell) and runs the
//! script with NO arguments, a bare command that has no way to be left unterminated.

use crate::harness_log;

const DIR: &str = ".harness";
pub const PATH: &str = ".harness/inbox.json";

// Trail of the last consumed envelope — avoids reprocessing a stale JSON if the script
// runs twice without a rewrite, and doubles as a diagnostic.
pub const CONSUMED_PATH: &str = ".harness/inbox.consumed.json";

/// Raw inbox content, or `""` if it doesn't exist. Parsing/sanitization lives in `Envelope`.
pub fn read() -> String {
    let p = std::path::Path::new(PATH);
    if p.exists() {
        match std::fs::read_to_string(p) {
            Ok(content) => return content,
            Err(e) => harness_log::error(&format!("[Inbox] failed to read {PATH}: {e}")),
        }
    }
    String::new()
}

/// Moves the consumed inbox to `CONSUMED_PATH` after a successful parse.
pub fn consume() {
    let p = std::path::Path::new(PATH);
    if p.exists() {
        if let Err(e) = std::fs::create_dir_all(DIR) {
            harness_log::error(&format!("[Inbox] failed to consume {PATH}: {e}"));
            return;
        }
        if let Err(e) = std::fs::rename(p, CONSUMED_PATH) {
            harness_log::error(&format!("[Inbox] failed to consume {PATH}: {e}"));
        }
    }
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
    fn read_sem_arquivo_retorna_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert_eq!(read(), "");
    }

    #[test]
    fn read_com_arquivo_devolve_o_conteudo_bruto() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::create_dir_all(DIR).unwrap();
        std::fs::write(PATH, r#"{"type":"text","value":"start"}"#).unwrap();

        assert_eq!(read(), r#"{"type":"text","value":"start"}"#);
    }

    #[test]
    fn consume_move_a_inbox_para_o_caminho_consumido() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::create_dir_all(DIR).unwrap();
        std::fs::write(PATH, "{}").unwrap();

        consume();

        assert!(!std::path::Path::new(PATH).exists());
        assert!(std::path::Path::new(CONSUMED_PATH).exists());
    }

    #[test]
    fn consume_sem_arquivo_nao_panica() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        consume();

        assert!(!std::path::Path::new(PATH).exists());
    }
}
