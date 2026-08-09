//! Pure, deterministic gate for global plan revisions. It judges evidence and invariants
//! only; it does not interpret whether a technical strategy is semantically good — that
//! judgment stays with the driver. The harness decides ACCEPTANCE, not quality.

use std::collections::{HashMap, HashSet, VecDeque};

use serde::{Deserialize, Serialize};

use crate::feature_store::{Feature, PlanRevision};
use crate::plan_observation_store::PlanObservation;

/// Deviation from `.NET`: the reference serializes this enum as a plain integer (no
/// `JsonStringEnumConverter` registered there). This is an engine-internal audit field
/// (persisted only in this port's own `.harness/plans/plan-v*.json`, never read by another
/// language's port or by the driver), so a readable string tag is strictly better here and
/// costs nothing in cross-port compatibility.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PlanRevisionVerdict {
    Approve,
    ApproveWithWarnings,
    Reject,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PlanRevisionIssue {
    pub code: String,
    pub message: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct PlanDiff {
    pub added: Vec<i32>,
    pub removed: Vec<i32>,
    pub modified: Vec<i32>,
    pub reprioritized: Vec<i32>,
    #[serde(rename = "removedReferences")]
    pub removed_references: Vec<String>,
    #[serde(rename = "removedAcceptanceCriteria")]
    pub removed_acceptance_criteria: Vec<String>,
}

impl PlanDiff {
    pub fn has_changes(&self) -> bool {
        !self.added.is_empty() || !self.removed.is_empty() || !self.modified.is_empty() || !self.reprioritized.is_empty()
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PlanRevisionEvaluation {
    pub verdict: PlanRevisionVerdict,
    pub errors: Vec<PlanRevisionIssue>,
    pub warnings: Vec<PlanRevisionIssue>,
    pub diff: PlanDiff,
}

impl PlanRevisionEvaluation {
    pub fn passed(&self) -> bool {
        self.verdict != PlanRevisionVerdict::Reject
    }
}

fn push_error(errors: &mut Vec<PlanRevisionIssue>, code: &str, message: String) {
    errors.push(PlanRevisionIssue {
        code: code.to_string(),
        message,
    });
}

/// `current` is the live (pre-revision) feature list; `revision` is what the driver
/// proposed; `observations` is the full persisted evidence log; `max_features` is the
/// flow's feature-count ceiling; `remaining_steps` is the run's remaining step budget
/// (negative disables the budget-risk warning — mirrors `.NET`'s `remainingSteps >= 0`
/// guard); `steps_per_feature` is the per-feature step ceiling.
pub fn evaluate(
    current: &[Feature],
    revision: &PlanRevision,
    observations: &[PlanObservation],
    max_features: usize,
    remaining_steps: i32,
    steps_per_feature: i32,
) -> PlanRevisionEvaluation {
    let mut errors: Vec<PlanRevisionIssue> = Vec::new();
    let mut warnings: Vec<PlanRevisionIssue> = Vec::new();

    let proposed: &[Feature] = &revision.revised_features;
    let current_by_id: HashMap<i32, &Feature> = current.iter().map(|f| (f.id, f)).collect();
    let mut proposed_by_id: HashMap<i32, &Feature> = HashMap::new();
    for f in proposed {
        proposed_by_id.entry(f.id).or_insert(f);
    }
    let diff = build_diff(current, proposed);

    if revision.reason.trim().is_empty() {
        push_error(&mut errors, "REVISION_REASON_REQUIRED", "A revision reason is required.".to_string());
    }

    let mut seen_alternatives = HashSet::new();
    let alternatives: Vec<String> = revision
        .alternatives_considered
        .iter()
        .filter(|a| !a.trim().is_empty())
        .map(|a| normalize(a))
        .filter(|a| seen_alternatives.insert(a.clone()))
        .collect();
    if alternatives.len() < 2 {
        push_error(&mut errors, "ALTERNATIVES_REQUIRED", "At least two distinct alternatives are required.".to_string());
    }

    if proposed.is_empty() {
        push_error(&mut errors, "PLAN_EMPTY", "The revised plan must contain features.".to_string());
    }
    if proposed.len() > max_features {
        push_error(&mut errors, "FEATURE_LIMIT", format!("The revised plan exceeds the {max_features}-feature limit."));
    }
    if proposed.iter().any(|f| f.id <= 0 || f.title.trim().is_empty() || f.priority <= 0) {
        push_error(
            &mut errors,
            "FEATURE_INVALID",
            "Every feature needs a positive unique id, title and positive priority.".to_string(),
        );
    }
    {
        let mut seen_ids = HashSet::new();
        let unique_count = proposed.iter().filter(|f| seen_ids.insert(f.id)).count();
        if unique_count != proposed.len() {
            push_error(&mut errors, "FEATURE_ID_DUPLICATE", "Feature ids must be unique.".to_string());
        }
    }

    for passed in current.iter().filter(|f| f.passes) {
        match proposed_by_id.get(&passed.id) {
            None => push_error(&mut errors, "PASSED_FEATURE_REMOVED", format!("Passed feature #{} cannot be removed.", passed.id)),
            Some(retained) => {
                if !same_definition(passed, retained) {
                    push_error(&mut errors, "PASSED_FEATURE_MODIFIED", format!("Passed feature #{} cannot be modified.", passed.id));
                }
            }
        }
    }

    let passed_ids: HashSet<i32> = current.iter().filter(|f| f.passes).map(|f| f.id).collect();
    validate_graph(proposed, &passed_ids, &mut errors);

    for reference in &diff.removed_references {
        push_error(&mut errors, "REQUIREMENT_COVERAGE_REMOVED", format!("Brief reference '{reference}' is no longer covered."));
    }
    for acceptance in &diff.removed_acceptance_criteria {
        push_error(&mut errors, "ACCEPTANCE_REMOVED", format!("Acceptance criterion '{acceptance}' is no longer covered."));
    }

    let known_observation_ids: HashSet<&str> = observations.iter().map(|o| o.id.as_str()).collect();
    if revision.based_on_observation_ids.is_empty() {
        push_error(&mut errors, "OBSERVATION_REQUIRED", "The revision must cite at least one persisted observation.".to_string());
    }
    let mut seen_observations = HashSet::new();
    for id in revision.based_on_observation_ids.iter().filter(|id| seen_observations.insert(id.as_str())) {
        if !known_observation_ids.contains(id.as_str()) {
            push_error(&mut errors, "OBSERVATION_UNKNOWN", format!("Observation '{id}' does not exist in the run evidence."));
        }
    }

    if !diff.has_changes() {
        push_error(&mut errors, "PLAN_UNCHANGED", "The revision does not change the current plan.".to_string());
    }
    let fingerprint = crate::plan_revision_store::fingerprint(proposed);
    if crate::plan_revision_store::has_plan_fingerprint(&fingerprint) {
        push_error(&mut errors, "PLAN_REPEATED", "The same revised plan was already applied in this run.".to_string());
    }

    let pending = proposed
        .iter()
        .filter(|f| !current_by_id.get(&f.id).map(|old| old.passes).unwrap_or(false))
        .count() as i32;
    let worst_case_steps = pending * steps_per_feature;
    if remaining_steps >= 0 && worst_case_steps > remaining_steps {
        warnings.push(PlanRevisionIssue {
            code: "BUDGET_RISK".to_string(),
            message: format!("The revised plan may require {worst_case_steps} steps with {remaining_steps} remaining."),
        });
    }

    let verdict = if !errors.is_empty() {
        PlanRevisionVerdict::Reject
    } else if !warnings.is_empty() {
        PlanRevisionVerdict::ApproveWithWarnings
    } else {
        PlanRevisionVerdict::Approve
    };

    PlanRevisionEvaluation {
        verdict,
        errors,
        warnings,
        diff,
    }
}

fn build_diff(current: &[Feature], proposed: &[Feature]) -> PlanDiff {
    let before: HashMap<i32, &Feature> = current.iter().map(|f| (f.id, f)).collect();
    let mut after: HashMap<i32, &Feature> = HashMap::new();
    for f in proposed {
        after.entry(f.id).or_insert(f);
    }

    let mut added: Vec<i32> = after.keys().filter(|id| !before.contains_key(id)).copied().collect();
    added.sort_unstable();
    let mut removed: Vec<i32> = before.keys().filter(|id| !after.contains_key(id)).copied().collect();
    removed.sort_unstable();

    let common: Vec<i32> = before.keys().filter(|id| after.contains_key(id)).copied().collect();
    let mut reprioritized: Vec<i32> = common
        .iter()
        .filter(|id| before[id].priority != after[id].priority)
        .copied()
        .collect();
    reprioritized.sort_unstable();
    let mut modified: Vec<i32> = common
        .iter()
        .filter(|id| !same_definition_except_priority(before[id], after[id]))
        .copied()
        .collect();
    modified.sort_unstable();

    let old_refs = casefold_set(current.iter().flat_map(|f| f.references.iter().cloned()));
    let new_refs = casefold_set(proposed.iter().flat_map(|f| f.references.iter().cloned()));
    let mut removed_references: Vec<String> = old_refs
        .iter()
        .filter(|(key, _)| !new_refs.contains_key(*key))
        .map(|(_, original)| original.clone())
        .collect();
    removed_references.sort();

    let old_acceptance = casefold_set(
        current
            .iter()
            .flat_map(|f| f.implementation_context.acceptance.iter())
            .filter(|v| !v.trim().is_empty())
            .map(|v| normalize(v)),
    );
    let new_acceptance = casefold_set(
        proposed
            .iter()
            .flat_map(|f| f.implementation_context.acceptance.iter())
            .filter(|v| !v.trim().is_empty())
            .map(|v| normalize(v)),
    );
    let mut removed_acceptance_criteria: Vec<String> = old_acceptance
        .iter()
        .filter(|(key, _)| !new_acceptance.contains_key(*key))
        .map(|(_, original)| original.clone())
        .collect();
    removed_acceptance_criteria.sort();

    PlanDiff {
        added,
        removed,
        modified,
        reprioritized,
        removed_references,
        removed_acceptance_criteria,
    }
}

// Case-insensitive set that keeps the first-seen original casing per key — mirrors C#'s
// `HashSet<string>(StringComparer.OrdinalIgnoreCase)`, which compares case-insensitively
// but preserves the string as originally inserted.
fn casefold_set(values: impl Iterator<Item = String>) -> HashMap<String, String> {
    let mut map = HashMap::new();
    for value in values {
        if value.trim().is_empty() {
            continue;
        }
        map.entry(value.to_lowercase()).or_insert(value);
    }
    map
}

// Collapses runs of whitespace to single spaces and trims — same behavior as C#'s
// `string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))`.
fn normalize(value: &str) -> String {
    value.split_whitespace().collect::<Vec<_>>().join(" ")
}

// `None`/no-op equivalent: if duplicate ids are present the FEATURE_ID_DUPLICATE error was
// already raised above and the graph can't be meaningfully validated — mirrors `.NET`'s
// early return.
fn validate_graph(features: &[Feature], passed_ids: &HashSet<i32>, errors: &mut Vec<PlanRevisionIssue>) {
    let mut seen_ids = HashSet::new();
    let distinct_count = features.iter().filter(|f| seen_ids.insert(f.id)).count();
    if distinct_count != features.len() {
        return;
    }

    let ids: HashSet<i32> = features.iter().map(|f| f.id).collect();
    for feature in features {
        if feature.depends_on.contains(&feature.id) {
            push_error(errors, "SELF_DEPENDENCY", format!("Feature #{} depends on itself.", feature.id));
        }
        for &missing in feature.depends_on.iter().filter(|d| !ids.contains(d)) {
            push_error(errors, "DEPENDENCY_MISSING", format!("Feature #{} depends on missing feature #{missing}.", feature.id));
        }
    }
    if features.iter().flat_map(|f| f.depends_on.iter()).any(|id| !ids.contains(id)) {
        return;
    }

    let mut indegree: HashMap<i32, i32> = HashMap::new();
    let mut dependents: HashMap<i32, Vec<i32>> = HashMap::new();
    for f in features {
        let distinct_deps: HashSet<i32> = f.depends_on.iter().copied().collect();
        indegree.insert(f.id, distinct_deps.len() as i32);
        for dep in distinct_deps {
            dependents.entry(dep).or_default().push(f.id);
        }
    }

    let mut queue: VecDeque<i32> = indegree.iter().filter(|&(_, &d)| d == 0).map(|(&id, _)| id).collect();
    let mut resolved = 0i32;
    while let Some(id) = queue.pop_front() {
        resolved += 1;
        if let Some(deps) = dependents.get(&id) {
            for &dependent in deps {
                if let Some(d) = indegree.get_mut(&dependent) {
                    *d -= 1;
                    if *d == 0 {
                        queue.push_back(dependent);
                    }
                }
            }
        }
    }
    if resolved as usize != features.len() {
        push_error(errors, "DEPENDENCY_CYCLE", "The revised dependency graph contains a cycle.".to_string());
    }

    let pending: Vec<&Feature> = features.iter().filter(|f| !passed_ids.contains(&f.id)).collect();
    if !pending.is_empty() && !pending.iter().any(|f| f.depends_on.iter().all(|d| passed_ids.contains(d))) {
        push_error(errors, "PLAN_NO_READY_FEATURE", "The revised plan has pending work but no executable feature.".to_string());
    }
}

// Deliberately duplicated from `feature_store`'s own `same_definition` — see that module's
// comment: each store/evaluator owns its own copy of the invariant it enforces.
fn same_definition(left: &Feature, right: &Feature) -> bool {
    left.priority == right.priority && same_definition_except_priority(left, right)
}

fn same_definition_except_priority(left: &Feature, right: &Feature) -> bool {
    left.id == right.id
        && left.title == right.title
        && left.description == right.description
        && left.depends_on == right.depends_on
        && left.references == right.references
        && left.implementation_context.requirements == right.implementation_context.requirements
        && left.implementation_context.constraints == right.implementation_context.constraints
        && left.implementation_context.files == right.implementation_context.files
        && left.implementation_context.acceptance == right.implementation_context.acceptance
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::feature_store::ImplementationContext;
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

    fn feature_with_coverage(id: i32, title: &str, priority: i32, passes: bool) -> Feature {
        Feature {
            passes,
            references: vec!["RF-001".to_string()],
            implementation_context: ImplementationContext {
                acceptance: vec!["returns HTTP 401".to_string()],
                ..Default::default()
            },
            ..feature(id, title, priority)
        }
    }

    fn revision(features: Vec<Feature>, observation_id: &str) -> PlanRevision {
        PlanRevision {
            reason: "new evidence requires a global dependency".to_string(),
            alternatives_considered: vec![
                "keep the stub".to_string(),
                "introduce the dependency; selected because it preserves behavior".to_string(),
            ],
            revised_features: features,
            based_on_observation_ids: vec![observation_id.to_string()],
        }
    }

    #[test]
    fn aprova_mudanca_rastreavel_que_preserva_cobertura() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let observation = crate::plan_observation_store::append("verification_failure", Some(2), "API verification failed repeatedly.", &["exit 1"]);

        let current = vec![feature_with_coverage(1, "API", 2, false)];
        let revised = revision(
            vec![
                Feature {
                    depends_on: vec![2],
                    ..feature_with_coverage(1, "API", 2, false)
                },
                feature(2, "Authentication", 1),
            ],
            &observation.id,
        );

        let result = evaluate(&current, &revised, &crate::plan_observation_store::load(), 10, 80, 8);

        assert_eq!(result.verdict, PlanRevisionVerdict::Approve);
        assert!(result.errors.is_empty());
        assert_eq!(result.diff.added, vec![2]);
        assert_eq!(result.diff.modified, vec![1]);
    }

    #[test]
    fn rejeita_perda_de_referencia_e_acceptance() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let observation = crate::plan_observation_store::append("verification_failure", Some(2), "API verification failed repeatedly.", &["exit 1"]);

        let current = vec![feature_with_coverage(1, "API", 2, false)];
        let revised = revision(vec![feature(1, "API reduced", 2)], &observation.id);

        let result = evaluate(&current, &revised, &crate::plan_observation_store::load(), 10, 80, 8);

        assert_eq!(result.verdict, PlanRevisionVerdict::Reject);
        assert!(result.errors.iter().any(|e| e.code == "REQUIREMENT_COVERAGE_REMOVED"));
        assert!(result.errors.iter().any(|e| e.code == "ACCEPTANCE_REMOVED"));
    }

    #[test]
    fn rejeita_observacao_desconhecida_e_plano_sem_mudanca() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let current = vec![feature(1, "API", 1)];
        let revised = PlanRevision {
            reason: "retry".to_string(),
            alternatives_considered: vec!["A".to_string(), "B".to_string()],
            revised_features: current.clone(),
            based_on_observation_ids: vec!["OBS-999".to_string()],
        };

        let result = evaluate(&current, &revised, &[], 10, 80, 8);

        assert!(result.errors.iter().any(|e| e.code == "OBSERVATION_UNKNOWN"));
        assert!(result.errors.iter().any(|e| e.code == "PLAN_UNCHANGED"));
    }

    #[test]
    fn aprova_com_aviso_quando_orcamento_pode_ser_insuficiente() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let observation = crate::plan_observation_store::append("verification_failure", Some(2), "API verification failed repeatedly.", &["exit 1"]);

        let current = vec![feature(1, "API", 1)];
        let revised = revision(vec![feature(1, "API", 2), feature(2, "Auth", 1)], &observation.id);

        let result = evaluate(&current, &revised, &crate::plan_observation_store::load(), 10, 4, 8);

        assert_eq!(result.verdict, PlanRevisionVerdict::ApproveWithWarnings);
        assert!(result.warnings.iter().any(|w| w.code == "BUDGET_RISK"));
    }

    #[test]
    fn rejeita_plano_ja_aplicado_no_historico() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let observation = crate::plan_observation_store::append("verification_failure", Some(2), "API verification failed repeatedly.", &["exit 1"]);

        let current = vec![feature(1, "API", 2)];
        let revised = revision(
            vec![
                Feature {
                    depends_on: vec![2],
                    ..feature(1, "API", 2)
                },
                feature(2, "Auth", 1),
            ],
            &observation.id,
        );
        let first = evaluate(&current, &revised, &crate::plan_observation_store::load(), 10, 80, 8);
        crate::plan_revision_store::record(&revised, &revised.revised_features, &first);

        let repeated = evaluate(&current, &revised, &crate::plan_observation_store::load(), 10, 80, 8);

        assert!(repeated.errors.iter().any(|e| e.code == "PLAN_REPEATED"));
    }

    #[test]
    fn rejeita_remocao_de_feature_passada() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let observation = crate::plan_observation_store::append("verification_failure", Some(2), "API verification failed repeatedly.", &["exit 1"]);

        let current = vec![feature_with_coverage(1, "API", 2, true), feature(2, "Auth", 1)];
        let revised = revision(vec![feature(2, "Auth", 1)], &observation.id);

        let result = evaluate(&current, &revised, &crate::plan_observation_store::load(), 10, 80, 8);

        assert_eq!(result.verdict, PlanRevisionVerdict::Reject);
        assert!(result.errors.iter().any(|e| e.code == "PASSED_FEATURE_REMOVED"));
    }

    #[test]
    fn rejeita_ciclo_de_dependencia() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let observation = crate::plan_observation_store::append("verification_failure", Some(2), "API verification failed repeatedly.", &["exit 1"]);

        let current = vec![feature(1, "A", 1)];
        let revised = revision(
            vec![
                Feature {
                    depends_on: vec![2],
                    ..feature(1, "A", 1)
                },
                Feature {
                    depends_on: vec![1],
                    ..feature(2, "B", 2)
                },
            ],
            &observation.id,
        );

        let result = evaluate(&current, &revised, &crate::plan_observation_store::load(), 10, 80, 8);

        assert_eq!(result.verdict, PlanRevisionVerdict::Reject);
        assert!(result.errors.iter().any(|e| e.code == "DEPENDENCY_CYCLE"));
    }
}
