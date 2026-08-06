//! Append-only, human-readable engine log at `.harness/harness.log` — persisted
//! counterpart to what today only reaches ephemeral stderr (`error`), plus the step
//! entry/exit markers (`info`, written by `task_registry::dispatch`) that make an
//! in-flight step observable before it completes. `trace` only records a COMPLETED turn —
//! during a slow step, or one that faults mid-flight, `trace.jsonl` alone gives no
//! evidence the harness ever picked up the work. This file is that evidence.
//!
//! Deliberately separate from `trace.jsonl`: the trace is a hash-chained,
//! one-line-per-turn audit artifact consumed by evaluators and cost-correlation tooling —
//! doubling it with entry/exit lines would break that "one line = one turn" contract for
//! every consumer. `harness.log` carries no such contract; it's free-form and append-only.

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/harness.log";

/// Truncates the log at the start of a new workflow (alongside `trace::reset`).
pub fn reset() {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        if let Err(e) = std::fs::remove_file(p) {
            eprintln!("[HarnessLog] failed to clear: {e}");
        }
    }
}

/// Liveness/diagnostic events (step entry/exit) — file only, no stderr echo per turn.
pub fn info(message: &str) {
    write_line("INFO", message);
}

/// Every harness-level failure — protocol errors, guard cutoffs, store I/O failures,
/// unhandled faults. Writes to stderr too (existing visible behavior every call site
/// already relied on) so this is a drop-in replacement for the raw `eprintln!` calls
/// scattered across the engine.
pub fn error(message: &str) {
    eprintln!("{message}");
    write_line("ERROR", message);
}

fn write_line(level: &str, message: &str) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[HarnessLog] failed to write: {e}");
        return;
    }

    let timestamp = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Micros, false);
    let line = format!("[{timestamp}] [{level}] {message}\n");

    use std::io::Write;
    let file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(FILE_PATH);
    match file {
        Ok(mut f) => {
            if let Err(e) = f.write_all(line.as_bytes()) {
                eprintln!("[HarnessLog] failed to write: {e}");
            }
        }
        Err(e) => eprintln!("[HarnessLog] failed to write: {e}"),
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
    fn info_grava_uma_linha_com_nivel_e_mensagem() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        info("[step 1] enter 'start'");

        let content = std::fs::read_to_string(FILE_PATH).unwrap();
        assert!(content.contains("[INFO]"));
        assert!(content.contains("[step 1] enter 'start'"));
    }

    #[test]
    fn error_grava_no_arquivo() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        error("[harness] something failed");

        let content = std::fs::read_to_string(FILE_PATH).unwrap();
        assert!(content.contains("[ERROR]"));
        assert!(content.contains("[harness] something failed"));
    }

    #[test]
    fn reset_apaga_o_arquivo() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        info("first run");
        assert!(std::path::Path::new(FILE_PATH).exists());

        reset();

        assert!(!std::path::Path::new(FILE_PATH).exists());
    }

    #[test]
    fn reset_sem_arquivo_ainda_nao_entra_em_panico() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        reset();
    }
}
