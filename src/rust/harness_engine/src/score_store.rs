//! Persists the result of each evaluation to `.harness/scores.jsonl` (one line per
//! run). This is the "scores" side of telemetry, consumed by reports.

use serde::{Deserialize, Serialize};

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/scores.jsonl";

/// Score for an evaluation: the deterministic gate's verdict (0 tokens) and, when it
/// passes, the LLM judge's score. `judge_score` = 0 when the gate fails.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ScoreReport {
    #[serde(default)]
    pub timestamp: String,
    #[serde(rename = "gatePassed", default)]
    pub gate_passed: bool,
    #[serde(rename = "gateDetail", default)]
    pub gate_detail: String,
    #[serde(rename = "judgeScore", default)]
    pub judge_score: i32,
    #[serde(rename = "judgeRationale", default)]
    pub judge_rationale: String,
}

pub fn append(report: &ScoreReport) {
    if let Err(e) = std::fs::create_dir_all(DIR) {
        eprintln!("[ScoreStore] failed to write: {e}");
        return;
    }
    let line = match serde_json::to_string(report) {
        Ok(l) => l,
        Err(e) => {
            eprintln!("[ScoreStore] failed to write: {e}");
            return;
        }
    };

    use std::io::Write;
    match std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(FILE_PATH)
    {
        Ok(mut f) => {
            if let Err(e) = writeln!(f, "{line}") {
                eprintln!("[ScoreStore] failed to write: {e}");
            }
        }
        Err(e) => eprintln!("[ScoreStore] failed to write: {e}"),
    }
}

pub fn load() -> Vec<ScoreReport> {
    let p = std::path::Path::new(FILE_PATH);
    if !p.exists() {
        return Vec::new();
    }

    match std::fs::read_to_string(p) {
        Ok(content) => content
            .lines()
            .filter(|line| !line.trim().is_empty())
            .filter_map(|line| serde_json::from_str::<ScoreReport>(line).ok())
            .collect(),
        Err(e) => {
            eprintln!("[ScoreStore] failed to load: {e}");
            Vec::new()
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
    fn append_e_load_fazem_roundtrip() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append(&ScoreReport {
            timestamp: "2026-07-25T00:00:00Z".to_string(),
            gate_passed: true,
            gate_detail: "ok".to_string(),
            judge_score: 8,
            judge_rationale: "good coverage".to_string(),
        });

        let loaded = load();

        assert_eq!(loaded.len(), 1);
        assert!(loaded[0].gate_passed);
        assert_eq!(loaded[0].judge_score, 8);
    }

    #[test]
    fn load_sem_arquivo_retorna_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert!(load().is_empty());
    }
}
