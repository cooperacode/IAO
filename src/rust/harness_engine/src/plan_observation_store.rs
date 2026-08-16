//! Append-only evidence that may justify a global plan revision, persisted as JSON Lines
//! at `.harness/plan_observations.jsonl`. Never truncated except on a genuinely new run
//! (`reset`, called alongside `feature_store::reset`) — the replan gate needs the full
//! history to validate a proposal's cited `basedOnObservationIds`.

use serde::{Deserialize, Serialize};

use crate::harness_log;

const DIR: &str = ".harness";
const FILE_PATH: &str = ".harness/plan_observations.jsonl";

/// One persisted piece of evidence: what was observed (`kind`, free-form — e.g.
/// `"verification_failure"`), which feature it relates to (if any), a human summary, and
/// the raw evidence strings that back it up.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PlanObservation {
    pub id: String,
    pub kind: String,
    #[serde(rename = "featureId", default, skip_serializing_if = "Option::is_none")]
    pub feature_id: Option<i32>,
    pub summary: String,
    #[serde(default)]
    pub evidence: Vec<String>,
    // ISO 8601 UTC timestamp, stored as a string (parity with the wire JSON).
    #[serde(rename = "observedAt")]
    pub observed_at: String,
}

/// Appends one observation and returns it. Id format `OBS-{n:03}`, n = 1-based count of
/// observations already on file (before this one).
pub fn append(
    kind: &str,
    feature_id: Option<i32>,
    summary: &str,
    evidence: &[&str],
) -> PlanObservation {
    let observation = PlanObservation {
        id: format!("OBS-{:03}", load().len() + 1),
        kind: kind.to_string(),
        feature_id,
        summary: summary.to_string(),
        evidence: evidence
            .iter()
            .filter(|e| !e.trim().is_empty())
            .map(|e| e.to_string())
            .collect(),
        observed_at: chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Micros, false),
    };

    if let Err(e) = std::fs::create_dir_all(DIR) {
        harness_log::error(&format!("[PlanObservationStore] failed to append: {e}"));
        return observation;
    }

    match serde_json::to_string(&observation) {
        Ok(json) => {
            use std::io::Write;
            let file = std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(FILE_PATH);
            match file {
                Ok(mut f) => {
                    if let Err(e) = writeln!(f, "{json}") {
                        harness_log::error(&format!(
                            "[PlanObservationStore] failed to append: {e}"
                        ));
                    }
                }
                Err(e) => {
                    harness_log::error(&format!("[PlanObservationStore] failed to append: {e}"))
                }
            }
        }
        Err(e) => harness_log::error(&format!("[PlanObservationStore] failed to append: {e}")),
    }

    observation
}

/// Loads every persisted observation. Tolerant, line by line: a corrupt/partial final line
/// (interrupted mid-write) is skipped rather than failing the whole load.
pub fn load() -> Vec<PlanObservation> {
    let p = std::path::Path::new(FILE_PATH);
    if !p.exists() {
        return Vec::new();
    }
    let content = match std::fs::read_to_string(p) {
        Ok(c) => c,
        Err(e) => {
            harness_log::error(&format!("[PlanObservationStore] failed to load: {e}"));
            return Vec::new();
        }
    };
    content
        .lines()
        .filter(|line| !line.trim().is_empty())
        .filter_map(|line| serde_json::from_str::<PlanObservation>(line).ok())
        .collect()
}

/// Deletes the previous run's evidence — paired with `feature_store::reset`.
pub fn reset() {
    let p = std::path::Path::new(FILE_PATH);
    if p.exists() {
        if let Err(e) = std::fs::remove_file(p) {
            harness_log::error(&format!("[PlanObservationStore] failed to reset: {e}"));
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
    fn append_atribui_ids_sequenciais() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let first = append("verification_failure", Some(2), "first", &["evidence"]);
        let second = append("verification_failure", Some(2), "second", &["evidence"]);

        assert_eq!(first.id, "OBS-001");
        assert_eq!(second.id, "OBS-002");
    }

    #[test]
    fn append_e_load_fazem_roundtrip() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append(
            "missing_dependency",
            Some(3),
            "needs a foundation",
            &["compiler failure", ""],
        );

        let loaded = load();

        assert_eq!(loaded.len(), 1);
        assert_eq!(loaded[0].kind, "missing_dependency");
        assert_eq!(loaded[0].feature_id, Some(3));
        assert_eq!(loaded[0].evidence, vec!["compiler failure".to_string()]);
    }

    #[test]
    fn load_arquivo_ausente_retorna_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert!(load().is_empty());
    }

    #[test]
    fn load_tolera_linha_final_corrompida() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append("kind", None, "ok", &[]);
        std::fs::create_dir_all(DIR).unwrap();
        {
            use std::io::Write;
            let mut f = std::fs::OpenOptions::new()
                .append(true)
                .open(FILE_PATH)
                .unwrap();
            writeln!(f, "not valid json").unwrap();
        }

        let loaded = load();

        assert_eq!(loaded.len(), 1);
    }

    #[test]
    fn reset_apaga_o_arquivo() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        append("kind", None, "summary", &[]);
        reset();

        assert!(load().is_empty());
    }

    #[test]
    fn reset_sem_arquivo_nao_panica() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        reset();
    }
}
