//! Records one line per loop iteration in `.harness/trace.jsonl`. This is the basis for
//! both telemetry and the trajectory evaluator: `state_store` only keeps the final state —
//! it overwrites `data` on every step —, so without this recorded sequence there's no way
//! to evaluate the path the agent took.
//!
//! Cost: zero tokens and one append write per invocation.

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/trace.jsonl";

/// Trajectory frozen from the last run that ended in `stop`. `harness_host` writes here
/// when the producer flow completes, so another flow (the evaluation) can read the
/// evidence even after resetting the live `trace.jsonl` in its own `start`.
pub const LAST_RUN_PATH: &str = ".harness/last-run.trace.jsonl";

/// Trajectory frozen from the last evaluation run. Its own path so the evaluation (which
/// also ends in `stop`) doesn't overwrite the run's evidence in `LAST_RUN_PATH`.
pub const LAST_EVALUATION_PATH: &str = ".harness/last-evaluation.trace.jsonl";

/// Possible outcomes of a step, recorded in `TraceEntry::outcome`.
pub mod trace_outcome {
    pub const INSTRUCTION: &str = "instruction"; // proceeded to the next step
    pub const STOP: &str = "stop"; // normal flow termination
    pub const ERROR: &str = "error"; // typed error returned to the driver
    pub const BUDGET: &str = "budget"; // cut off by the step ceiling
    pub const TIMEOUT: &str = "timeout"; // cut off by the per-step time ceiling
}

/// One loop iteration: step, received command, outcome, cost (chars of the emitted
/// instruction), and recording time.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TraceEntry {
    pub step: i32,
    pub command: String,
    pub outcome: String,
    #[serde(rename = "instructionChars")]
    pub instruction_chars: i32,
    // ISO 8601 with offset, stored as a string (parity with the wire JSON).
    pub timestamp: String,
    /// Integrity hash-chain: SHA-256 hex of the trace's previous JSON line (genesis — the
    /// first line, or an empty/missing trace — is 64 zeros). Editing or removing an
    /// earlier entry breaks the chain of subsequent entries, making trace tampering
    /// detectable. `#[serde(default)]` to accept traces written by earlier versions of
    /// the harness, which lack this field.
    #[serde(rename = "prevHash", default)]
    pub prev_hash: String,
    /// Optional, domain-agnostic label (e.g. "feature:3") that solves the same pain point
    /// as `state_store`: `step` is a counter global to the whole run, it doesn't identify
    /// WHICH unit of work the step belongs to. `trace` just carries the value — it's the
    /// flow that decides what it means (see flows_development::tasks::pick).
    /// `#[serde(default)]` for the same reason as `prev_hash`: parity with traces written
    /// before this field existed.
    #[serde(default)]
    pub label: String,
}

fn genesis_hash() -> String {
    "0".repeat(64)
}

fn sha256_hex(value: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(value.as_bytes());
    format!("{:x}", hasher.finalize())
}

/// Hash of the last non-empty line of the trace at the given `path` — the next entry's
/// `prev_hash`. Genesis (64 zeros) if the file doesn't exist, is empty, or was just
/// reset.
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

/// Truncates the trace at the start of a new workflow (alongside `state_store::reset`).
pub fn reset() {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        if let Err(e) = std::fs::remove_file(p) {
            eprintln!("[Trace] failed to clear: {e}");
        }
    }
}

/// Label-less convenience — equivalent to `append_with_label(.., "")`. Kept so not every
/// call site (including tests) is forced to pass a fifth argument they don't use.
pub fn append(step: i32, command: &str, outcome: &str, instruction_chars: i32) {
    append_with_label(step, command, outcome, instruction_chars, "");
}

pub fn append_with_label(
    step: i32,
    command: &str,
    outcome: &str,
    instruction_chars: i32,
    label: &str,
) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[Trace] failed to write: {e}");
        return;
    }

    let entry = TraceEntry {
        step,
        command: command.to_string(),
        outcome: outcome.to_string(),
        instruction_chars,
        timestamp: now_iso(),
        prev_hash: last_line_hash(FILE_PATH),
        label: label.to_string(),
    };

    let mut line = match serde_json::to_string(&entry) {
        Ok(line) => line,
        Err(e) => {
            eprintln!("[Trace] failed to write: {e}");
            return;
        }
    };
    line.push('\n');

    // A single `write_all` with the complete line (JSON + newline) already assembled —
    // don't split it into multiple writes, so the event stays atomic even under an
    // interruption mid-call (log appends across different lines are still not atomic
    // relative to each other, but each line itself is never partially written).
    use std::io::Write;
    let file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(FILE_PATH);
    match file {
        Ok(mut f) => {
            if let Err(e) = f.write_all(line.as_bytes()) {
                eprintln!("[Trace] failed to write: {e}");
            }
        }
        Err(e) => eprintln!("[Trace] failed to write: {e}"),
    }
}

/// Freezes the live trace at the destination path — the evidence of the completed run.
pub fn snapshot(destination: &str) {
    if std::path::Path::new(FILE_PATH).exists() {
        if let Err(e) = std::fs::create_dir_all(DIR) {
            eprintln!("[Trace] failed to freeze: {e}");
            return;
        }
        if let Err(e) = std::fs::copy(FILE_PATH, destination) {
            eprintln!("[Trace] failed to freeze: {e}");
        }
    }
}

/// Re-reads the live trace in the order it was written.
pub fn load() -> Vec<TraceEntry> {
    load_from(FILE_PATH)
}

/// Re-reads a trace from an arbitrary path — input for the evaluators (e.g. the snapshot).
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
