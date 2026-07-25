//! Canal de entrada por arquivo — alternativa ao argv para o envelope do turno.
//!
//! O transporte por argumento single-quoted (`./run-development-rs.sh '<JSON>'`) tem uma
//! falha estrutural: se o driver-LLM esquece a aspa de fechamento, o shell entra em modo
//! de continuação e trava ANTES do binário rodar — nenhuma validação da engine pode
//! pegá-lo. A inbox tira o payload da sintaxe de aspas do shell: o agente escreve o JSON
//! aqui com sua ferramenta de escrita de arquivo (não passa por shell) e roda o script SEM
//! argumentos, um comando bare que não tem como ficar não-terminado.

const DIR: &str = ".harness";
pub const PATH: &str = ".harness/inbox.json";

// Rastro do último envelope consumido — evita reprocessar um JSON velho se o script
// rodar duas vezes sem reescrita, e serve de diagnóstico.
pub const CONSUMED_PATH: &str = ".harness/inbox.consumed.json";

/// Conteúdo bruto da inbox, ou `""` se ela não existir. O parse/sanitização fica no `Envelope`.
pub fn read() -> String {
    let p = std::path::Path::new(PATH);
    if p.exists() {
        match std::fs::read_to_string(p) {
            Ok(content) => return content,
            Err(e) => eprintln!("[Inbox] falha ao ler {PATH}: {e}"),
        }
    }
    String::new()
}

/// Move a inbox consumida para `CONSUMED_PATH` após um parse bem-sucedido.
pub fn consume() {
    let p = std::path::Path::new(PATH);
    if p.exists() {
        if let Err(e) = std::fs::create_dir_all(DIR) {
            eprintln!("[Inbox] falha ao consumir {PATH}: {e}");
            return;
        }
        if let Err(e) = std::fs::rename(p, CONSUMED_PATH) {
            eprintln!("[Inbox] falha ao consumir {PATH}: {e}");
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
