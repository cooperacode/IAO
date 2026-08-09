//! Persists proposed and accepted global plan revisions for audit and resume.
//!
//! `PROPOSAL_PATH` (`.harness/replan.json`) is the driver-written proposal, read once by
//! `flows_development::tasks::replan` and validated by `plan_revision_evaluator`.
//! `CURRENT_PATH` (`.harness/plan_revision.json`) is the "pointer" to the latest applied
//! revision (its `version` is `revision_count()`); `DIR` (`.harness/plans/`) keeps one
//! `plan-v{N}.json` per applied revision — full history, used both for audit and to detect
//! a repeated proposal via `fingerprint`/`has_plan_fingerprint`.

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::feature_store::{Feature, FeatureList, PlanRevision};
use crate::harness_log;
use crate::plan_revision_evaluator::PlanRevisionEvaluation;

pub const PROPOSAL_PATH: &str = ".harness/replan.json";
const DIR: &str = ".harness/plans";
const CURRENT_PATH: &str = ".harness/plan_revision.json";

/// One applied revision: its 1-based version, when it landed, why, what alternatives were
/// weighed, which observations justified it, a content fingerprint of the resulting plan
/// (dedup guard), the evaluation that approved it, and the resulting feature list itself.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AppliedPlanRevision {
    pub version: i32,
    #[serde(rename = "appliedAt")]
    pub applied_at: String,
    pub reason: String,
    #[serde(rename = "alternativesConsidered")]
    pub alternatives_considered: Vec<String>,
    #[serde(rename = "basedOnObservationIds")]
    pub based_on_observation_ids: Vec<String>,
    #[serde(rename = "planFingerprint")]
    pub plan_fingerprint: String,
    pub evaluation: PlanRevisionEvaluation,
    pub features: Vec<Feature>,
}

/// Reads the driver-written proposal at `PROPOSAL_PATH`. `None` if absent or unreadable —
/// the caller (`tasks::replan`) treats that as "no proposal found" and re-requests one.
pub fn read_proposal() -> Option<PlanRevision> {
    let p = std::path::Path::new(PROPOSAL_PATH);
    if !p.exists() {
        return None;
    }
    match std::fs::read_to_string(p) {
        Ok(content) => match serde_json::from_str::<PlanRevision>(&content) {
            Ok(revision) => Some(revision),
            Err(e) => {
                harness_log::error(&format!("[PlanRevisionStore] failed to read proposal: {e}"));
                None
            }
        },
        Err(e) => {
            harness_log::error(&format!("[PlanRevisionStore] failed to read proposal: {e}"));
            None
        }
    }
}

/// How many revisions have been applied so far in this run — the `version` of the current
/// applied-revision pointer, or 0 if none has landed yet.
pub fn revision_count() -> i32 {
    let p = std::path::Path::new(CURRENT_PATH);
    if !p.exists() {
        return 0;
    }
    match std::fs::read_to_string(p) {
        Ok(content) => serde_json::from_str::<AppliedPlanRevision>(&content).map(|a| a.version).unwrap_or(0),
        Err(_) => 0,
    }
}

/// Records an accepted revision: bumps the version, writes the "current" pointer, and
/// appends a dedicated `plan-v{N}.json` to the history directory.
pub fn record(revision: &PlanRevision, features: &[Feature], evaluation: &PlanRevisionEvaluation) {
    let applied = AppliedPlanRevision {
        version: revision_count() + 1,
        applied_at: chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Micros, false),
        reason: revision.reason.clone(),
        alternatives_considered: revision.alternatives_considered.clone(),
        based_on_observation_ids: revision.based_on_observation_ids.clone(),
        plan_fingerprint: fingerprint(features),
        evaluation: evaluation.clone(),
        features: features.to_vec(),
    };

    if let Err(e) = std::fs::create_dir_all(DIR) {
        harness_log::error(&format!("[PlanRevisionStore] failed to record: {e}"));
        return;
    }
    let json = match serde_json::to_string_pretty(&applied) {
        Ok(j) => j,
        Err(e) => {
            harness_log::error(&format!("[PlanRevisionStore] failed to record: {e}"));
            return;
        }
    };
    if let Err(e) = crate::atomic_io::write_atomic(std::path::Path::new(CURRENT_PATH), &json) {
        harness_log::error(&format!("[PlanRevisionStore] failed to record: {e}"));
    }
    let version_path = format!("{DIR}/plan-v{}.json", applied.version);
    if let Err(e) = crate::atomic_io::write_atomic(std::path::Path::new(&version_path), &json) {
        harness_log::error(&format!("[PlanRevisionStore] failed to record: {e}"));
    }
}

/// Whether `fingerprint` already matches some applied revision's `plan_fingerprint` — the
/// dedup guard that rejects the identical proposal reapplied within the same run. Tolerant
/// of a corrupt history entry (skips it, never approves a proposal because of it).
pub fn has_plan_fingerprint(fingerprint: &str) -> bool {
    let dir = std::path::Path::new(DIR);
    if !dir.exists() {
        return false;
    }
    let entries = match std::fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => return false,
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("");
        if !name.starts_with("plan-v") || !name.ends_with(".json") {
            continue;
        }
        if let Ok(content) = std::fs::read_to_string(&path) {
            if let Ok(applied) = serde_json::from_str::<AppliedPlanRevision>(&content) {
                if applied.plan_fingerprint == fingerprint {
                    return true;
                }
            }
        }
    }
    false
}

/// Content fingerprint of a feature list: sort by id, force `passes = false` in the
/// canonical form (so a plan doesn't get a fresh fingerprint just because a feature in it
/// has since passed), serialize the same shape `feature_list.json` uses, SHA-256, lowercase
/// hex. Deviation from `.NET`: the byte-for-byte JSON encoding differs (Rust's serde vs.
/// .NET's source-generated `System.Text.Json`), so the resulting hex digest differs too —
/// harmless, since each port's `.harness/plans/` history is local to that port's own run
/// and never compared across languages; only self-consistency within one process matters.
pub fn fingerprint(features: &[Feature]) -> String {
    let mut canonical: Vec<Feature> = features.to_vec();
    canonical.sort_by_key(|f| f.id);
    for f in &mut canonical {
        f.passes = false;
    }
    let json = serde_json::to_string(&FeatureList { items: canonical }).unwrap_or_default();
    let mut hasher = Sha256::new();
    hasher.update(json.as_bytes());
    format!("{:x}", hasher.finalize())
}

/// Deletes the previous run's proposal, current pointer, and revision history — paired
/// with `feature_store::reset`.
pub fn reset() {
    for path in [PROPOSAL_PATH, CURRENT_PATH] {
        let p = std::path::Path::new(path);
        if p.exists() {
            if let Err(e) = std::fs::remove_file(p) {
                harness_log::error(&format!("[PlanRevisionStore] failed to reset: {e}"));
            }
        }
    }
    let dir = std::path::Path::new(DIR);
    if dir.exists() {
        if let Err(e) = std::fs::remove_dir_all(dir) {
            harness_log::error(&format!("[PlanRevisionStore] failed to reset: {e}"));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::feature_store::ImplementationContext;
    use crate::plan_revision_evaluator::{PlanDiff, PlanRevisionVerdict};
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

    fn feature(id: i32, title: &str, priority: i32) -> Feature {
        Feature {
            id,
            title: title.to_string(),
            priority,
            passes: false,
            depends_on: Vec::new(),
            description: String::new(),
            references: Vec::new(),
            implementation_context: ImplementationContext::default(),
        }
    }

    fn approval() -> PlanRevisionEvaluation {
        PlanRevisionEvaluation {
            verdict: PlanRevisionVerdict::Approve,
            errors: Vec::new(),
            warnings: Vec::new(),
            diff: PlanDiff::default(),
        }
    }

    fn revision() -> PlanRevision {
        PlanRevision {
            reason: "reason".to_string(),
            alternatives_considered: vec!["A".to_string(), "B".to_string()],
            revised_features: vec![feature(1, "A", 1)],
            based_on_observation_ids: vec!["OBS-001".to_string()],
        }
    }

    #[test]
    fn read_proposal_ausente_retorna_none() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert!(read_proposal().is_none());
    }

    #[test]
    fn read_proposal_le_o_arquivo_gravado() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        std::fs::create_dir_all(".harness").unwrap();
        std::fs::write(PROPOSAL_PATH, r#"{"reason":"x","alternativesConsidered":["A","B"],"revisedFeatures":[{"id":1,"title":"A","priority":1}],"basedOnObservationIds":["OBS-001"]}"#).unwrap();

        let revision = read_proposal().unwrap();

        assert_eq!(revision.reason, "x");
        assert_eq!(revision.revised_features.len(), 1);
    }

    #[test]
    fn revision_count_sem_historico_e_zero() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        assert_eq!(revision_count(), 0);
    }

    #[test]
    fn record_incrementa_versao_e_grava_historico() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let revision = revision();

        record(&revision, &revision.revised_features, &approval());
        assert_eq!(revision_count(), 1);
        assert!(std::path::Path::new(".harness/plans/plan-v1.json").exists());

        record(&revision, &revision.revised_features, &approval());
        assert_eq!(revision_count(), 2);
        assert!(std::path::Path::new(".harness/plans/plan-v2.json").exists());
    }

    #[test]
    fn fingerprint_e_estavel_e_ignora_passes() {
        let a = vec![feature(1, "A", 1)];
        let mut b = a.clone();
        b[0].passes = true;

        assert_eq!(fingerprint(&a), fingerprint(&b));
    }

    #[test]
    fn fingerprint_muda_com_o_conteudo() {
        let a = vec![feature(1, "A", 1)];
        let b = vec![feature(1, "B", 1)];

        assert_ne!(fingerprint(&a), fingerprint(&b));
    }

    #[test]
    fn has_plan_fingerprint_detecta_reaplicacao() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let revision = revision();
        let fp = fingerprint(&revision.revised_features);
        assert!(!has_plan_fingerprint(&fp));

        record(&revision, &revision.revised_features, &approval());

        assert!(has_plan_fingerprint(&fp));
    }

    #[test]
    fn reset_apaga_proposta_ponteiro_e_historico() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let revision = revision();
        std::fs::create_dir_all(".harness").unwrap();
        std::fs::write(PROPOSAL_PATH, "{}").unwrap();
        record(&revision, &revision.revised_features, &approval());

        reset();

        assert!(!std::path::Path::new(PROPOSAL_PATH).exists());
        assert!(!std::path::Path::new(CURRENT_PATH).exists());
        assert!(!std::path::Path::new(DIR).exists());
        assert_eq!(revision_count(), 0);
    }

    #[test]
    fn reset_sem_arquivos_nao_panica() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        reset();
    }
}
