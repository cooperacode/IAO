//! Grava uma linha por volta do loop em `.harness/trace.jsonl`. É a base tanto da
//! telemetria quanto do evaluator de trajetória: `state_store` guarda só o estado final —
//! sobrescreve `data` a cada passo —, então sem esta sequência gravada não há como avaliar
//! o caminho que o agente percorreu.
//!
//! Custo: zero token e uma escrita append por invocação.

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/trace.jsonl";

/// Trajetória congelada do último run que terminou em `stop`. `harness_host` grava aqui ao
/// concluir o flow produtor, para que outro flow (a avaliação) leia a evidência mesmo
/// depois de resetar o `trace.jsonl` vivo no próprio `start`.
pub const LAST_RUN_PATH: &str = ".harness/last-run.trace.jsonl";

/// Trajetória congelada do último run de avaliação. Caminho próprio para que a avaliação
/// (que também termina em `stop`) não sobrescreva a evidência do run em `LAST_RUN_PATH`.
pub const LAST_EVALUATION_PATH: &str = ".harness/last-evaluation.trace.jsonl";

/// Desfechos possíveis de um passo, gravados em `TraceEntry::outcome`.
pub mod trace_outcome {
    pub const INSTRUCTION: &str = "instruction"; // seguiu para o próximo passo
    pub const STOP: &str = "stop"; // término normal do flow
    pub const ERROR: &str = "error"; // erro tipado devolvido ao driver
    pub const BUDGET: &str = "budget"; // corte pelo teto de passos
    pub const TIMEOUT: &str = "timeout"; // corte pelo teto de tempo por passo
}

/// Uma volta do loop: passo, comando recebido, desfecho, custo (chars da instrução
/// emitida) e horário de gravação.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TraceEntry {
    pub step: i32,
    pub command: String,
    pub outcome: String,
    #[serde(rename = "instructionChars")]
    pub instruction_chars: i32,
    // ISO 8601 com offset, gravado como string (paridade com o wire JSON).
    pub timestamp: String,
    /// Hash-chain de integridade: SHA-256 hex da linha JSON anterior do trace (genesis —
    /// primeira linha ou trace vazio/ausente — é 64 zeros). Qualquer edição ou remoção de
    /// uma entrada anterior quebra o encadeamento das entradas seguintes, tornando
    /// adulteração do trace detectável. `#[serde(default)]` para aceitar traces gravados
    /// por versões anteriores do harness, sem este campo.
    #[serde(rename = "prevHash", default)]
    pub prev_hash: String,
}

fn genesis_hash() -> String {
    "0".repeat(64)
}

fn sha256_hex(value: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(value.as_bytes());
    format!("{:x}", hasher.finalize())
}

/// Hash da última linha não-vazia do trace no `path` informado — `prev_hash` da próxima
/// entrada. Genesis (64 zeros) se o arquivo não existe, está vazio ou acabou de ser
/// resetado.
fn last_line_hash(path: &str) -> String {
    let p = std::path::Path::new(path);
    if !p.exists() {
        return genesis_hash();
    }
    match std::fs::read_to_string(p) {
        Ok(content) => match content.lines().filter(|l| !l.trim().is_empty()).last() {
            Some(last) => sha256_hex(last),
            None => genesis_hash(),
        },
        Err(_) => genesis_hash(),
    }
}

/// Trunca o trace no início de um novo workflow (junto do `state_store::reset`).
pub fn reset() {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        if let Err(e) = std::fs::remove_file(p) {
            eprintln!("[Trace] falha ao limpar: {e}");
        }
    }
}

pub fn append(step: i32, command: &str, outcome: &str, instruction_chars: i32) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[Trace] falha ao gravar: {e}");
        return;
    }

    let entry = TraceEntry {
        step,
        command: command.to_string(),
        outcome: outcome.to_string(),
        instruction_chars,
        timestamp: now_iso(),
        prev_hash: last_line_hash(FILE_PATH),
    };

    let mut line = match serde_json::to_string(&entry) {
        Ok(line) => line,
        Err(e) => {
            eprintln!("[Trace] falha ao gravar: {e}");
            return;
        }
    };
    line.push('\n');

    // Uma única `write_all` com a linha completa (JSON + newline) já montada — não
    // fragmentar em múltiplas escritas, para que o evento fique atômico mesmo sob
    // interrupção a meio da chamada (append de log continua não-atômico entre linhas
    // diferentes, mas cada linha em si não fica parcialmente gravada).
    use std::io::Write;
    let file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(FILE_PATH);
    match file {
        Ok(mut f) => {
            if let Err(e) = f.write_all(line.as_bytes()) {
                eprintln!("[Trace] falha ao gravar: {e}");
            }
        }
        Err(e) => eprintln!("[Trace] falha ao gravar: {e}"),
    }
}

/// Congela o trace vivo no caminho de destino — a evidência do run concluído.
pub fn snapshot(destination: &str) {
    if std::path::Path::new(FILE_PATH).exists() {
        if let Err(e) = std::fs::create_dir_all(DIR) {
            eprintln!("[Trace] falha ao congelar: {e}");
            return;
        }
        if let Err(e) = std::fs::copy(FILE_PATH, destination) {
            eprintln!("[Trace] falha ao congelar: {e}");
        }
    }
}

/// Relê o trace vivo na ordem em que foi gravado.
pub fn load() -> Vec<TraceEntry> {
    load_from(FILE_PATH)
}

/// Relê um trace de um caminho arbitrário — insumo dos evaluators (ex.: o snapshot).
pub fn load_from(path: &str) -> Vec<TraceEntry> {
    let p = std::path::Path::new(path);
    if !p.exists() {
        return Vec::new();
    }

    match std::fs::read_to_string(p) {
        Ok(content) => content
            .lines()
            .filter(|line| !line.trim().is_empty())
            .filter_map(|line| serde_json::from_str::<TraceEntry>(line).ok())
            .collect(),
        Err(e) => {
            eprintln!("[Trace] falha ao carregar: {e}");
            Vec::new()
        }
    }
}

fn now_iso() -> String {
    chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Micros, false)
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
    fn append_e_load_fazem_roundtrip_na_ordem_de_gravacao() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append(1, "start", trace_outcome::INSTRUCTION, 42);
        append(2, "classify", trace_outcome::INSTRUCTION, 99);

        let entries = load();

        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].step, 1);
        assert_eq!(entries[0].command, "start");
        assert_eq!(entries[0].outcome, trace_outcome::INSTRUCTION);
        assert_eq!(entries[0].instruction_chars, 42);
        assert_eq!(entries[1].step, 2);
        assert_eq!(entries[1].command, "classify");
        assert_eq!(entries[1].instruction_chars, 99);
        assert!(!entries[0].timestamp.is_empty());
    }

    #[test]
    fn primeira_entrada_do_trace_tem_prev_hash_genesis() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append(1, "start", trace_outcome::INSTRUCTION, 1);

        assert_eq!(load()[0].prev_hash, "0".repeat(64));
    }

    #[test]
    fn cada_entrada_encadeia_prev_hash_com_sha256_da_linha_anterior() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append(1, "start", trace_outcome::INSTRUCTION, 42);
        append(2, "classify", trace_outcome::INSTRUCTION, 99);
        append(3, "finalize", trace_outcome::STOP, 5);

        let raw_lines: Vec<String> = std::fs::read_to_string(FILE_PATH)
            .unwrap()
            .lines()
            .map(|l| l.to_string())
            .collect();
        let entries = load();

        let mut hasher = Sha256::new();
        hasher.update(raw_lines[0].as_bytes());
        assert_eq!(entries[1].prev_hash, format!("{:x}", hasher.finalize()));

        let mut hasher = Sha256::new();
        hasher.update(raw_lines[1].as_bytes());
        assert_eq!(entries[2].prev_hash, format!("{:x}", hasher.finalize()));
    }

    #[test]
    fn reset_seguido_de_append_reinicia_a_cadeia_com_genesis() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append(1, "start", trace_outcome::INSTRUCTION, 1);
        append(2, "classify", trace_outcome::INSTRUCTION, 1);

        reset();
        append(1, "start", trace_outcome::INSTRUCTION, 1);

        assert_eq!(load().len(), 1);
        assert_eq!(load()[0].prev_hash, "0".repeat(64));
    }

    #[test]
    fn deserializa_trace_legado_sem_prev_hash_com_default_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::create_dir_all(DIR).unwrap();
        std::fs::write(
            FILE_PATH,
            r#"{"step":1,"command":"start","outcome":"instruction","instructionChars":1,"timestamp":"2026-01-01T00:00:00Z"}"#,
        )
        .unwrap();

        let entries = load();

        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].prev_hash, "");
    }

    #[test]
    fn load_sem_arquivo_retorna_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert!(load().is_empty());
    }

    #[test]
    fn reset_trunca_o_trace_anterior() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append(1, "start", trace_outcome::INSTRUCTION, 1);
        assert_eq!(load().len(), 1);

        reset();

        assert!(load().is_empty());
    }

    #[test]
    fn snapshot_copia_o_trace_vivo_para_o_destino() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append(1, "start", trace_outcome::INSTRUCTION, 1);

        snapshot(LAST_RUN_PATH);

        assert_eq!(load_from(LAST_RUN_PATH).len(), 1);
    }
}
