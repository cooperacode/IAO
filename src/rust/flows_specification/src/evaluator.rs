//! Evaluators (mirrors `SpecificationEvaluator.cs`). Pure: no I/O, no rendering — only
//! `store`'s plain JSON-value predicates (`non_empty`/`text_present`/`duplicate_ids`) are
//! reused here to avoid duplicating them, same helpers `store` itself uses for JSON shape
//! checks.

use crate::store::{duplicate_ids, non_empty, text_present};
use serde_json::Value;
use std::collections::{HashMap, HashSet, VecDeque};

pub(crate) const IDEA_SCHEMA: &str = "iao/idea/v1";
pub(crate) const PRD_SCHEMA: &str = "iao/prd/v1";
pub(crate) const SRS_SCHEMA: &str = "iao/srs/v1";
pub(crate) const SDD_SCHEMA: &str = "iao/sdd/v1";
pub(crate) const MAX_IDEA_UTF8_BYTES: usize = 20_000;
pub(crate) const MAX_DESIGN_CONTENT_UTF8_BYTES: usize = 1_000_000;

pub(crate) fn validate_idea(value: &Value, source: &str, source_digest: &str) -> Vec<String> {
    let mut errors = Vec::new();
    if value["schema"] != IDEA_SCHEMA {
        errors.push("IDEA_SCHEMA_UNKNOWN".into());
    }
    for key in ["title", "problem"] {
        if !text_present(value, key) {
            errors.push(format!("IDEA_{}_MISSING", key.to_uppercase()));
        }
    }
    for key in ["users", "desiredOutcomes"] {
        if !non_empty(value, key) {
            errors.push(format!("IDEA_{}_MISSING", key.to_uppercase()));
        }
    }
    if duplicate_ids(value.get("openQuestions").and_then(Value::as_array)) {
        errors.push("IDEA_OPEN_QUESTION_DUPLICATE_ID".into());
    }
    if serde_json::to_vec(value).map_or(0, |bytes| bytes.len()) > MAX_IDEA_UTF8_BYTES {
        errors.push("IDEA_TOO_LARGE".into());
    }
    if source.trim().is_empty() {
        errors.push("IDEA_SOURCE_MISSING".into());
    }
    if source_digest.trim().is_empty() {
        errors.push("IDEA_DIGEST_MISSING".into());
    }
    errors
}

pub(crate) fn validate_prd(value: &Value, parent_digest: &str) -> Vec<String> {
    let mut errors = Vec::new();
    if value["schema"] != PRD_SCHEMA {
        errors.push("PRD_SCHEMA_UNKNOWN".into());
    }
    if value["ideaDigest"] != parent_digest {
        errors.push("PRD_IDEA_DIGEST_STALE".into());
    }
    for (key, code) in [
        ("goals", "PRD_GOAL_ID_DUPLICATE"),
        ("successMetrics", "PRD_METRIC_ID_DUPLICATE"),
        ("risks", "PRD_RISK_ID_DUPLICATE"),
        ("decisions", "PRD_DECISION_ID_DUPLICATE"),
    ] {
        if duplicate_ids(value.get(key).and_then(Value::as_array)) {
            errors.push(code.into());
        }
    }
    for (key, code) in [
        ("goals", "PRD_GOALS_EMPTY"),
        ("nonGoals", "PRD_NON_GOALS_EMPTY"),
        ("scope", "PRD_SCOPE_EMPTY"),
        ("successMetrics", "PRD_METRICS_EMPTY"),
    ] {
        if !non_empty(value, key) {
            errors.push(code.into());
        }
    }
    if let Some(metrics) = value["successMetrics"].as_array() {
        for metric in metrics {
            let measure = metric["measure"].as_str().unwrap_or("").trim();
            let target = metric["target"].as_str().unwrap_or("").trim();
            if measure.is_empty() || target.is_empty() || measure.eq_ignore_ascii_case(target) {
                errors.push("PRD_METRIC_MEASURE_TARGET_NOT_SEPARATE".into());
            }
        }
    }
    if value["openQuestions"].as_array().is_some_and(|items| {
        items
            .iter()
            .any(|item| item["blocking"].as_bool().unwrap_or(false))
    }) {
        errors.push("PRD_OPEN_QUESTION_BLOCKING".into());
    }
    errors
}

pub(crate) fn validate_srs(value: &Value, parent_digest: &str, goal_ids: &[String]) -> Vec<String> {
    let mut errors = Vec::new();
    if value["schema"] != SRS_SCHEMA {
        errors.push("SRS_SCHEMA_UNKNOWN".into());
    }
    if value["prdDigest"] != parent_digest {
        errors.push("SRS_PRD_DIGEST_STALE".into());
    }
    let requirements: Vec<&Value> = value["functionalRequirements"]
        .as_array()
        .into_iter()
        .flatten()
        .chain(
            value["qualityRequirements"]
                .as_array()
                .into_iter()
                .flatten(),
        )
        .collect();
    let ids: HashSet<&str> = requirements
        .iter()
        .filter_map(|item| item["id"].as_str())
        .collect();
    let covered: HashSet<&str> = requirements
        .iter()
        .flat_map(|item| item["goalIds"].as_array().into_iter().flatten())
        .filter_map(Value::as_str)
        .collect();
    if requirements
        .iter()
        .filter_map(|item| item["id"].as_str())
        .collect::<Vec<_>>()
        .len()
        != ids.len()
    {
        errors.push("SRS_REQUIREMENT_ID_DUPLICATE".into());
    }
    for goal in goal_ids {
        if !covered.contains(goal.as_str()) {
            errors.push("SRS_GOAL_NOT_COVERED".into());
        }
    }
    let acceptance: HashSet<&str> = value["acceptanceCriteria"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|item| item["id"].as_str())
        .collect();
    for requirement in requirements {
        if !text_present(requirement, "statement") {
            errors.push("SRS_REQUIREMENT_STATEMENT_MISSING".into());
        }
        if !non_empty(requirement, "acceptanceIds") {
            errors.push("SRS_REQUIREMENT_WITHOUT_ACCEPTANCE".into());
        }
        for acceptance_id in requirement["acceptanceIds"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if !acceptance.contains(acceptance_id) {
                errors.push("SRS_ACCEPTANCE_REFERENCE_DANGLING".into());
            }
        }
        for dependency in requirement["dependsOn"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if !ids.contains(dependency) {
                errors.push("SRS_REQUIREMENT_DEPENDENCY_DANGLING".into());
            }
        }
    }
    for criterion in value["acceptanceCriteria"].as_array().into_iter().flatten() {
        if !text_present(criterion, "given")
            || !text_present(criterion, "when")
            || !text_present(criterion, "then")
        {
            errors.push("SRS_ACCEPTANCE_CRITERION_TEXT_MISSING".into());
        }
        for requirement_id in criterion["requirementIds"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if !ids.contains(requirement_id) {
                errors.push("SRS_ACCEPTANCE_CRITERION_REQUIREMENT_DANGLING".into());
            }
        }
    }
    for iface in value["interfaces"].as_array().into_iter().flatten() {
        if !text_present(iface, "name") || !text_present(iface, "description") {
            errors.push("SRS_INTERFACE_TEXT_MISSING".into());
        }
        for requirement_id in iface["requirementIds"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if !ids.contains(requirement_id) {
                errors.push("SRS_INTERFACE_REQUIREMENT_DANGLING".into());
            }
        }
    }
    for rule in value["dataRules"].as_array().into_iter().flatten() {
        if !text_present(rule, "rule") {
            errors.push("SRS_DATA_RULE_TEXT_MISSING".into());
        }
        for requirement_id in rule["requirementIds"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if !ids.contains(requirement_id) {
                errors.push("SRS_DATA_RULE_REQUIREMENT_DANGLING".into());
            }
        }
    }
    let delivery = &value["delivery"];
    if !text_present(delivery, "target") || !text_present(delivery, "verificationStrategy") {
        errors.push("SRS_DELIVERY_TEXT_MISSING".into());
    }
    errors
}

pub(crate) fn validate_sdd(
    value: &Value,
    parent_digest: &str,
    requirement_ids: &[String],
) -> Vec<String> {
    validate_sdd_with_sources(value, parent_digest, requirement_ids, "", &[])
}

pub(crate) fn validate_sdd_with_sources(
    value: &Value,
    parent_digest: &str,
    requirement_ids: &[String],
    current_source_digest: &str,
    current_source_files: &[String],
) -> Vec<String> {
    let mut errors = Vec::new();
    if value["schema"] != SDD_SCHEMA {
        errors.push("SDD_SCHEMA_UNKNOWN".into());
    }
    if value["srsDigest"] != parent_digest {
        errors.push("SDD_SRS_DIGEST_STALE".into());
    }
    let adrs = value["adrs"].as_array().cloned().unwrap_or_default();
    if duplicate_ids(Some(&adrs)) {
        errors.push("SDD_ADR_ID_DUPLICATE".into());
    }
    let known: HashSet<&str> = requirement_ids.iter().map(String::as_str).collect();
    let allocated: HashSet<&str> = adrs
        .iter()
        .flat_map(|item| item["requirementIds"].as_array().into_iter().flatten())
        .filter_map(Value::as_str)
        .collect();
    for requirement in requirement_ids {
        if !allocated.contains(requirement.as_str()) {
            errors.push("SDD_REQUIREMENT_NOT_ALLOCATED".into());
        }
    }
    for adr in &adrs {
        for requirement_id in adr["requirementIds"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if !known.contains(requirement_id) {
                errors.push("SDD_ADR_REQUIREMENT_DANGLING".into());
            }
        }
    }
    let controls = value["controls"].as_array().cloned().unwrap_or_default();
    for control in &controls {
        for requirement_id in control["requirementIds"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if !known.contains(requirement_id) {
                errors.push("SDD_CONTROL_REQUIREMENT_DANGLING".into());
            }
        }
    }
    if let Some(content) = value["designContent"].as_str() {
        if !content.trim().is_empty() && content.len() > MAX_DESIGN_CONTENT_UTF8_BYTES {
            errors.push("SDD_DESIGN_CONTENT_TOO_LARGE".into());
        }
    }
    if !current_source_digest.trim().is_empty() {
        let source_digest = value["sourceDigest"].as_str().unwrap_or("").trim();
        if source_digest.is_empty() {
            errors.push("SDD_SOURCE_DIGEST_MISSING".into());
        } else if source_digest != current_source_digest {
            errors.push("SDD_SOURCE_DIGEST_STALE".into());
        }
        let source_files = value["sourceFiles"].as_array().cloned().unwrap_or_default();
        if source_files.is_empty() {
            errors.push("SDD_SOURCE_FILES_MISSING".into());
        } else if source_files.len() != current_source_files.len()
            || source_files
                .iter()
                .zip(current_source_files)
                .any(|(actual, expected)| actual.as_str() != Some(expected.as_str()))
        {
            errors.push("SDD_SOURCE_FILES_STALE".into());
        }
        if value["designContent"]
            .as_str()
            .map_or(true, |content| content.trim().is_empty())
        {
            errors.push("SDD_DESIGN_CONTENT_MISSING".into());
        }
    }
    errors
}

/// Kahn's algorithm over the slice `dependsOn` graph. Only edges pointing at another real
/// slice in the same proposal count — dangling/self references are already reported by
/// READINESS_DEPENDENCY_REFERENCE_DANGLING and must not also poison this check.
fn has_readiness_dependency_cycle(slices: &[Value]) -> bool {
    let mut indegree: HashMap<&str, i32> = HashMap::new();
    let mut adjacency: HashMap<&str, Vec<&str>> = HashMap::new();
    for slice in slices {
        if let Some(id) = slice["id"].as_str() {
            indegree.entry(id).or_insert(0);
            adjacency.entry(id).or_default();
        }
    }
    for slice in slices {
        let Some(id) = slice["id"].as_str() else {
            continue;
        };
        for dep in slice["dependsOn"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if dep != id && adjacency.contains_key(dep) {
                adjacency.get_mut(dep).unwrap().push(id);
                *indegree.get_mut(id).unwrap() += 1;
            }
        }
    }
    let mut queue: VecDeque<&str> = indegree
        .iter()
        .filter(|&(_, &v)| v == 0)
        .map(|(&k, _)| k)
        .collect();
    let mut visited = 0;
    while let Some(current) = queue.pop_front() {
        visited += 1;
        if let Some(next_list) = adjacency.get(current) {
            for &next in next_list {
                let entry = indegree.get_mut(next).unwrap();
                *entry -= 1;
                if *entry == 0 {
                    queue.push_back(next);
                }
            }
        }
    }
    visited < indegree.len()
}

pub(crate) fn validate_readiness(
    value: &Value,
    requirement_ids: &[String],
    adr_ids: &[String],
) -> Vec<String> {
    let mut errors = Vec::new();
    let verdict = value["verdict"].as_str().unwrap_or("");
    if !["READY", "FAIL:product", "FAIL:analysis", "FAIL:design"].contains(&verdict) {
        errors.push("READINESS_VERDICT_INVALID".into());
    }
    if value.get("conflicts").and_then(Value::as_array).is_none() {
        errors.push("READINESS_CONFLICTS_MISSING".into());
    }
    if value.get("residuals").and_then(Value::as_array).is_none() {
        errors.push("READINESS_RESIDUALS_MISSING".into());
    }
    if verdict != "READY" {
        return errors;
    }
    let slices = value["slices"].as_array().cloned().unwrap_or_default();
    if slices.is_empty() {
        errors.push("READINESS_SLICES_EMPTY".into());
    }
    if slices.len() > 10 {
        errors.push("READINESS_TOO_MANY_SLICES".into());
    }
    if duplicate_ids(Some(&slices)) {
        errors.push("READINESS_SLICE_ID_DUPLICATE".into());
    }
    let slice_ids: HashSet<&str> = slices
        .iter()
        .filter_map(|item| item["id"].as_str())
        .collect();
    for slice in &slices {
        for id in slice["requirementIds"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if !requirement_ids.iter().any(|known| known == id) {
                errors.push("READINESS_REQUIREMENT_REFERENCE_DANGLING".into());
            }
        }
        for id in slice["adrIds"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if !adr_ids.iter().any(|known| known == id) {
                errors.push("READINESS_ADR_REFERENCE_DANGLING".into());
            }
        }
        for dependency in slice["dependsOn"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(Value::as_str)
        {
            if dependency == slice["id"].as_str().unwrap_or("") || !slice_ids.contains(dependency) {
                errors.push("READINESS_DEPENDENCY_REFERENCE_DANGLING".into());
            }
        }
    }
    if has_readiness_dependency_cycle(&slices) {
        errors.push("READINESS_DEPENDENCY_CYCLE".into());
    }
    if !slices.is_empty()
        && !slices
            .iter()
            .any(|slice| slice["dependsOn"].as_array().is_some_and(Vec::is_empty))
    {
        errors.push("READINESS_NO_INITIAL_SLICE".into());
    }
    let covered: HashSet<&str> = slices
        .iter()
        .flat_map(|slice| slice["requirementIds"].as_array().into_iter().flatten())
        .filter_map(Value::as_str)
        .collect();
    for requirement in requirement_ids {
        if !covered.contains(requirement.as_str()) {
            errors.push("READINESS_REQUIREMENT_NOT_SLICED".into());
        }
    }
    errors
}

pub(crate) fn validate_approval(value: &Value, current_bundle_digest: &str) -> Vec<String> {
    let mut errors = Vec::new();
    let decision = value["decision"].as_str().unwrap_or("");
    if decision != "approved" && decision != "revise" {
        errors.push("APPROVAL_DECISION_INVALID".into());
    }
    let bundle_digest = value["bundleDigest"].as_str().unwrap_or("");
    if current_bundle_digest.is_empty() || bundle_digest != current_bundle_digest {
        errors.push("APPROVAL_BUNDLE_DIGEST_STALE".into());
    }
    if !text_present(value, "rationale") {
        errors.push("APPROVAL_RATIONALE_MISSING".into());
    }
    errors
}

/// Pre-publish gate (mirrors `SpecificationEvaluator.EvaluateDevelopmentReadiness`): bundle
/// digest freshness at the moment of approval, no blocking PRD open question, the readiness
/// slice graph itself (delegated to `validate_readiness`), and the rendered bundle's total
/// UTF-8 byte size against `docsMaxChars`.
#[allow(clippy::too_many_arguments)]
pub(crate) fn validate_development_readiness(
    prd: &Value,
    readiness: &Value,
    requirement_ids: &[String],
    adr_ids: &[String],
    bundle_digest_at_approval: &str,
    current_bundle_digest: &str,
    rendered: &HashMap<String, String>,
    docs_max_chars: i64,
) -> Vec<String> {
    let mut errors = Vec::new();
    if current_bundle_digest.is_empty() || bundle_digest_at_approval != current_bundle_digest {
        errors.push("DEV_READINESS_BUNDLE_DIGEST_STALE".into());
    }
    let has_blocking = prd["openQuestions"].as_array().is_some_and(|items| {
        items
            .iter()
            .any(|q| q["blocking"].as_bool().unwrap_or(false))
    });
    if has_blocking {
        errors.push("DEV_READINESS_BLOCKING_QUESTION_OPEN".into());
    }
    errors.extend(validate_readiness(readiness, requirement_ids, adr_ids));
    let total_bytes: i64 = rendered.values().map(|s| s.len() as i64).sum();
    if total_bytes > docs_max_chars {
        errors.push("DEV_READINESS_BUNDLE_TOO_LARGE".into());
    }
    errors
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn valid_srs() -> Value {
        json!({
            "schema": "iao/srs/v1",
            "prdDigest": "sha256:prd",
            "functionalRequirements": [
                {"id": "RF-001", "goalIds": ["OBJ-001"], "statement": "does the thing", "acceptanceIds": ["AC-001"]}
            ],
            "qualityRequirements": [],
            "acceptanceCriteria": [
                {"id": "AC-001", "requirementIds": ["RF-001"], "given": "given", "when": "when", "then": "then"}
            ],
            "interfaces": [],
            "dataRules": [],
            "delivery": {"target": "target", "verificationStrategy": "strategy", "isBootstrap": true}
        })
    }

    // Regression coverage for a real production crash in the .NET port that these checks
    // mirror: a DataRule with an empty/null "rule" used to sail through SRS evaluation, get
    // accepted, and only break much later — as a NullReferenceException inside the .NET
    // renderer's Escape() — when `approve` tried to publish it.
    #[test]
    fn validate_srs_documento_valido_passa() {
        let errors = validate_srs(&valid_srs(), "sha256:prd", &["OBJ-001".to_string()]);
        assert!(errors.is_empty(), "unexpected errors: {errors:?}");
    }

    #[test]
    fn validate_srs_regra_de_dados_sem_texto_e_rejeitada() {
        let mut document = valid_srs();
        document["dataRules"] =
            json!([{"id": "DR-001", "requirementIds": ["RF-001"], "rule": null}]);

        let errors = validate_srs(&document, "sha256:prd", &["OBJ-001".to_string()]);

        assert!(
            errors.contains(&"SRS_DATA_RULE_TEXT_MISSING".to_string()),
            "expected SRS_DATA_RULE_TEXT_MISSING, got {errors:?}"
        );
    }

    #[test]
    fn validate_srs_campos_de_texto_vazios_em_outros_registros_sao_rejeitados() {
        let mut document = valid_srs();
        document["functionalRequirements"][0]["statement"] = json!("   ");
        document["acceptanceCriteria"][0]["given"] = json!("");
        document["interfaces"] = json!([{"id": "IF-001", "requirementIds": ["RF-001"], "name": "name", "description": null}]);
        document["delivery"] =
            json!({"target": "", "verificationStrategy": "strategy", "isBootstrap": true});

        let errors = validate_srs(&document, "sha256:prd", &["OBJ-001".to_string()]);

        for code in [
            "SRS_REQUIREMENT_STATEMENT_MISSING",
            "SRS_ACCEPTANCE_CRITERION_TEXT_MISSING",
            "SRS_INTERFACE_TEXT_MISSING",
            "SRS_DELIVERY_TEXT_MISSING",
        ] {
            assert!(
                errors.contains(&code.to_string()),
                "expected {code} among {errors:?}"
            );
        }
    }

    #[test]
    fn validate_srs_referencia_dangling_em_acceptance_interface_e_data_rule_e_rejeitada() {
        let mut document = valid_srs();
        document["acceptanceCriteria"][0]["requirementIds"] = json!(["RF-999"]);
        document["interfaces"] = json!([{"id": "IF-001", "requirementIds": ["RF-999"], "name": "n", "description": "d"}]);
        document["dataRules"] =
            json!([{"id": "DR-001", "requirementIds": ["RF-999"], "rule": "r"}]);

        let errors = validate_srs(&document, "sha256:prd", &["OBJ-001".to_string()]);

        for code in [
            "SRS_ACCEPTANCE_CRITERION_REQUIREMENT_DANGLING",
            "SRS_INTERFACE_REQUIREMENT_DANGLING",
            "SRS_DATA_RULE_REQUIREMENT_DANGLING",
        ] {
            assert!(
                errors.contains(&code.to_string()),
                "expected {code} among {errors:?}"
            );
        }
    }

    #[test]
    fn validate_idea_sem_source_ou_digest_e_rejeitada() {
        let idea = json!({
            "schema": "iao/idea/v1",
            "title": "t",
            "problem": "p",
            "users": ["u"],
            "desiredOutcomes": ["o"],
            "openQuestions": []
        });

        let errors = validate_idea(&idea, "", "");

        assert!(errors.contains(&"IDEA_SOURCE_MISSING".to_string()));
        assert!(errors.contains(&"IDEA_DIGEST_MISSING".to_string()));
    }

    #[test]
    fn validate_idea_com_source_e_digest_nao_reporta_esses_codigos() {
        let idea = json!({
            "schema": "iao/idea/v1",
            "title": "t",
            "problem": "p",
            "users": ["u"],
            "desiredOutcomes": ["o"],
            "openQuestions": []
        });

        let errors = validate_idea(&idea, "specs/sources (1 file(s)): a.md", "sha256:abc");

        assert!(!errors.contains(&"IDEA_SOURCE_MISSING".to_string()));
        assert!(!errors.contains(&"IDEA_DIGEST_MISSING".to_string()));
    }

    fn valid_sdd() -> Value {
        json!({
            "schema": "iao/sdd/v1",
            "srsDigest": "sha256:srs",
            "adrs": [
                {"id": "ADR-1", "title": "t", "decision": "d", "rationale": "r", "requirementIds": ["RF-001"]}
            ],
            "controls": [
                {"id": "IC-1", "name": "n", "description": "d", "requirementIds": ["RF-001"]}
            ]
        })
    }

    #[test]
    fn validate_sdd_documento_valido_passa() {
        let errors = validate_sdd(&valid_sdd(), "sha256:srs", &["RF-001".to_string()]);
        assert!(errors.is_empty(), "unexpected errors: {errors:?}");
    }

    #[test]
    fn validate_sdd_adr_e_control_com_requisito_inexistente_sao_rejeitados() {
        let mut document = valid_sdd();
        document["adrs"][0]["requirementIds"] = json!(["RF-999"]);
        document["controls"][0]["requirementIds"] = json!(["RF-999"]);

        // RF-001 is now unallocated too (both adrs point at RF-999), so that code is also
        // expected — this test only asserts on the two dangling-reference codes it targets.
        let errors = validate_sdd(&document, "sha256:srs", &["RF-001".to_string()]);

        assert!(
            errors.contains(&"SDD_ADR_REQUIREMENT_DANGLING".to_string()),
            "{errors:?}"
        );
        assert!(
            errors.contains(&"SDD_CONTROL_REQUIREMENT_DANGLING".to_string()),
            "{errors:?}"
        );
    }

    #[test]
    fn validate_sdd_source_backed_design_passa() {
        let mut document = valid_sdd();
        document["sourceDigest"] = json!("sha256:sources");
        document["sourceFiles"] = json!(["architecture.md", "tree.txt"]);
        document["designContent"] = json!(
            "## Architecture\n\n```mermaid\ngraph TD\n```\n\n```text\napp/\n```"
        );

        let errors = validate_sdd_with_sources(
            &document,
            "sha256:srs",
            &["RF-001".to_string()],
            "sha256:sources",
            &["architecture.md".to_string(), "tree.txt".to_string()],
        );

        assert!(errors.is_empty(), "unexpected errors: {errors:?}");
    }

    #[test]
    fn validate_sdd_source_backed_design_sem_conteudo_e_rejeitado() {
        let mut document = valid_sdd();
        document["sourceDigest"] = json!("sha256:sources");
        document["sourceFiles"] = json!(["architecture.md"]);

        let errors = validate_sdd_with_sources(
            &document,
            "sha256:srs",
            &["RF-001".to_string()],
            "sha256:sources",
            &["architecture.md".to_string()],
        );

        assert!(
            errors.contains(&"SDD_DESIGN_CONTENT_MISSING".to_string()),
            "{errors:?}"
        );
    }

    fn slice(id: &str, depends_on: &[&str]) -> Value {
        json!({
            "id": id,
            "classification": "c",
            "goal": "g",
            "inScope": [],
            "outOfScope": [],
            "observableOutcome": "o",
            "requirementIds": [],
            "adrIds": [],
            "dependsOn": depends_on,
            "contracts": [],
            "happyPath": "h",
            "failurePath": "f",
            "acceptanceCriterion": "a",
            "suggestedTarget": "t",
            "suggestedVerificationStrategy": "v"
        })
    }

    #[test]
    fn validate_readiness_detecta_ciclo_de_dependencia() {
        let verdict = json!({
            "verdict": "READY",
            "conflicts": [],
            "residuals": [],
            "slices": [slice("SL-1", &["SL-2"]), slice("SL-2", &["SL-1"])]
        });

        let errors = validate_readiness(&verdict, &[], &[]);

        assert!(
            errors.contains(&"READINESS_DEPENDENCY_CYCLE".to_string()),
            "expected READINESS_DEPENDENCY_CYCLE among {errors:?}"
        );
    }

    #[test]
    fn validate_readiness_sem_ciclo_nao_reporta_o_codigo() {
        let verdict = json!({
            "verdict": "READY",
            "conflicts": [],
            "residuals": [],
            "slices": [slice("SL-1", &[]), slice("SL-2", &["SL-1"])]
        });

        let errors = validate_readiness(&verdict, &[], &[]);

        assert!(
            !errors.contains(&"READINESS_DEPENDENCY_CYCLE".to_string()),
            "{errors:?}"
        );
    }

    #[test]
    fn validate_approval_separa_os_tres_codigos() {
        let invalid_decision =
            json!({"decision": "maybe", "bundleDigest": "sha256:x", "rationale": "because"});
        let errors = validate_approval(&invalid_decision, "sha256:x");
        assert!(
            errors.contains(&"APPROVAL_DECISION_INVALID".to_string()),
            "{errors:?}"
        );
        assert!(
            !errors.contains(&"APPROVAL_BUNDLE_DIGEST_STALE".to_string()),
            "{errors:?}"
        );

        let stale_digest =
            json!({"decision": "approved", "bundleDigest": "sha256:old", "rationale": "because"});
        let errors = validate_approval(&stale_digest, "sha256:new");
        assert!(
            errors.contains(&"APPROVAL_BUNDLE_DIGEST_STALE".to_string()),
            "{errors:?}"
        );
        assert!(
            !errors.contains(&"APPROVAL_DECISION_INVALID".to_string()),
            "{errors:?}"
        );

        let missing_rationale =
            json!({"decision": "approved", "bundleDigest": "sha256:x", "rationale": ""});
        let errors = validate_approval(&missing_rationale, "sha256:x");
        assert!(
            errors.contains(&"APPROVAL_RATIONALE_MISSING".to_string()),
            "{errors:?}"
        );
    }
}
