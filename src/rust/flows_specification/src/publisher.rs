//! Publisher (mirrors `SpecificationPublisher.cs`): renders, stages, validates and publishes
//! the four accepted documents to `specs/active/`.

use crate::renderer::render_all;
use crate::store::{digest_str, read, write};
use serde_json::{Value, json};
use std::{
    collections::{HashMap, HashSet},
    fs,
    path::Path,
};

pub(crate) const DEST_DIR: &str = "specs/active";
pub(crate) const FILENAMES: [&str; 4] = [
    "00-prd.md",
    "10-software-requirements-specification.md",
    "20-software-design-document.md",
    "30-readiness-handoff.md",
];
pub(crate) const DEVELOPMENT_PLAN_FILENAME: &str = "40-development-plan.json";

fn expected_published_filenames() -> Vec<String> {
    FILENAMES
        .iter()
        .map(|name| (*name).to_string())
        .chain(std::iter::once(DEVELOPMENT_PLAN_FILENAME.to_string()))
        .collect()
}

fn development_plan(
    srs: &Value,
    sdd: &Value,
    readiness: &Value,
    rendered: &HashMap<String, String>,
) -> Value {
    let mut requirements = HashMap::new();
    for item in srs["functionalRequirements"]
        .as_array()
        .into_iter()
        .flatten()
        .chain(srs["qualityRequirements"].as_array().into_iter().flatten())
    {
        if let (Some(id), Some(statement)) = (item["id"].as_str(), item["statement"].as_str()) {
            requirements.insert(id.to_string(), statement.to_string());
        }
    }
    let mut adrs = HashMap::new();
    for item in sdd["adrs"].as_array().into_iter().flatten() {
        if let Some(id) = item["id"].as_str() {
            adrs.insert(id.to_string(), item);
        }
    }
    let slices = readiness["slices"].as_array().cloned().unwrap_or_default();
    let slice_ids: HashMap<String, i32> = slices
        .iter()
        .enumerate()
        .filter_map(|(index, item)| {
            item["id"]
                .as_str()
                .map(|id| (id.to_string(), index as i32 + 1))
        })
        .collect();
    let features: Vec<Value> = slices.iter().enumerate().map(|(index, item)| {
        let requirement_ids = item["requirementIds"].as_array().cloned().unwrap_or_default();
        let linked = |artifact: &Value| artifact["requirementIds"].as_array().into_iter().flatten().any(|id| requirement_ids.iter().any(|required| required == id));
        let acceptance_criteria: Vec<Value> = srs["acceptanceCriteria"].as_array().into_iter().flatten().filter(|artifact| linked(artifact)).cloned().collect();
        let interfaces: Vec<Value> = srs["interfaces"].as_array().into_iter().flatten().filter(|artifact| linked(artifact)).cloned().collect();
        let data_rules: Vec<Value> = srs["dataRules"].as_array().into_iter().flatten().filter(|artifact| linked(artifact)).cloned().collect();
        let controls: Vec<Value> = sdd["controls"].as_array().into_iter().flatten().filter(|artifact| linked(artifact)).cloned().collect();
        let adr_ids = item["adrIds"].as_array().cloned().unwrap_or_default();
        let requirements_text: Vec<Value> = requirement_ids.iter().filter_map(|value| value.as_str()).map(|id| json!(requirements.get(id).map(|text| format!("{id}: {text}")).unwrap_or_else(|| id.to_string()))).collect();
        let decisions: Vec<Value> = adr_ids.iter().filter_map(|value| value.as_str()).map(|id| {
            if let Some(adr) = adrs.get(id) { json!(format!("{id}: {}. Decision: {}. Rationale: {}", adr["title"].as_str().unwrap_or(""), adr["decision"].as_str().unwrap_or(""), adr["rationale"].as_str().unwrap_or("")))} else { json!(id) }
        }).collect();
        let mut references: Vec<Value> = requirement_ids.iter().chain(adr_ids.iter()).cloned().collect();
        for artifact in acceptance_criteria.iter().chain(interfaces.iter()).chain(data_rules.iter()).chain(controls.iter()) {
            if let Some(id) = artifact["id"].as_str() { references.push(json!(id)); }
        }
        let depends_on: Vec<Value> = item["dependsOn"].as_array().into_iter().flatten().filter_map(|value| value.as_str().and_then(|id| slice_ids.get(id)).map(|id| json!(id))).collect();
        let out_of_scope: Vec<Value> = item["outOfScope"].as_array().into_iter().flatten().filter_map(|value| value.as_str()).map(|value| json!(format!("out of scope: {value}"))).chain(item["contracts"].as_array().into_iter().flatten().cloned()).collect();
        let mut related_contracts = Vec::new();
        for artifact in interfaces.iter().chain(controls.iter()) {
            related_contracts.push(json!(format!(
                "{}: {}. {}",
                artifact["id"].as_str().unwrap_or(""),
                artifact["name"].as_str().unwrap_or(""),
                artifact["description"].as_str().unwrap_or("")
            )));
        }
        for artifact in &data_rules {
            related_contracts.push(json!(format!(
                "{}: {}",
                artifact["id"].as_str().unwrap_or(""),
                artifact["rule"].as_str().unwrap_or("")
            )));
        }
        let mut acceptance_text = vec![json!(item["acceptanceCriterion"].as_str().unwrap_or(""))];
        for artifact in &acceptance_criteria {
            acceptance_text.push(json!(format!(
                "{}: Given {}. When {}. Then {}",
                artifact["id"].as_str().unwrap_or(""),
                artifact["given"].as_str().unwrap_or(""),
                artifact["when"].as_str().unwrap_or(""),
                artifact["then"].as_str().unwrap_or("")
            )));
        }
        let goal = item["goal"].as_str().unwrap_or("");
        json!({
            "id": index + 1, "title": goal, "priority": index + 1, "passes": false,
            "dependsOn": depends_on,
            "description": format!("{goal} Observable outcome: {}. Happy path: {}. Failure path: {}.", item["observableOutcome"].as_str().unwrap_or(""), item["happyPath"].as_str().unwrap_or(""), item["failurePath"].as_str().unwrap_or("")),
            "references": references,
            "implementationContext": {"requirements": requirements_text, "decisions": decisions, "constraints": out_of_scope.into_iter().chain(related_contracts).collect::<Vec<_>>(), "files": [item["suggestedTarget"].as_str().unwrap_or("")], "acceptance": acceptance_text}
        })
    }).collect();
    let joined = FILENAMES
        .iter()
        .map(|name| rendered.get(*name).cloned().unwrap_or_default())
        .collect::<Vec<_>>()
        .join("|");
    json!({"schema": "iao/development-plan/v1", "specificationBundleDigest": digest_str(&joined), "sourceFiles": FILENAMES, "features": features, "targetDescription": srs["delivery"]["target"].as_str().unwrap_or(""), "verificationDescription": srs["delivery"]["verificationStrategy"].as_str().unwrap_or("")})
}

fn random_hex() -> String {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    format!("{:x}-{:x}", std::process::id(), nanos)
}

/// Independent postcondition check (blueprint 0004 §6 item 6): re-reads `specs/active/`
/// through the REAL `docs_reader::read` (never a re-implementation) and byte-compares its
/// output against what `publish()` just wrote — names/order, per-file digest, and that
/// DocsReader's concatenation contains each file's full on-disk text (detects truncation).
fn verify_postcondition(expected_digests: &HashMap<String, String>) -> Result<(), String> {
    let (content, files) = harness_engine::docs_reader::read(DEST_DIR);
    let expected: Vec<String> = FILENAMES.iter().map(|s| s.to_string()).collect();
    if files != expected {
        return Err(format!(
            "postcondition failed: DocsReader.Read('{DEST_DIR}') returned files [{}], expected [{}].",
            files.join(", "),
            expected.join(", ")
        ));
    }
    for name in FILENAMES {
        let file_path = format!("{DEST_DIR}/{name}");
        let text = fs::read_to_string(&file_path)
            .map_err(|e| format!("postcondition failed: could not read '{file_path}': {e}"))?;
        let actual_digest = digest_str(&text);
        let expected_digest = expected_digests.get(name).cloned().unwrap_or_default();
        if actual_digest != expected_digest {
            return Err(format!(
                "postcondition failed: '{name}' on-disk digest '{actual_digest}' does not match the digest recorded at publish time '{expected_digest}'."
            ));
        }
        if !content.contains(text.trim_end()) {
            return Err(format!(
                "postcondition failed: DocsReader.Read('{DEST_DIR}')'s content does not contain the full on-disk text of '{name}' (possible truncation)."
            ));
        }
    }
    Ok(())
}

/// Renders, stages, validates and — only if validation passes — publishes the four accepted
/// documents. Never deletes/globs `specs/active/`: every write is a named copy of one of the
/// four known filenames. The staging directory is always cleaned up (`finally`-equivalent via
/// the closure below), and a failed ownership check leaves the destination untouched.
pub(crate) fn publish(
    prd: &Value,
    srs: &Value,
    sdd: &Value,
    readiness: &Value,
) -> Result<HashMap<String, String>, String> {
    let mut rendered = render_all(prd, srs, sdd, readiness);
    let plan = serde_json::to_string(&development_plan(srs, sdd, readiness, &rendered))
        .map_err(|e| format!("failed to serialize development plan: {e}"))?;
    rendered.insert(DEVELOPMENT_PLAN_FILENAME.to_string(), plan);
    let expected_published = expected_published_filenames();
    let staging_dir = format!("{DEST_DIR}.staging-{}", random_hex());

    let result = (|| -> Result<HashMap<String, String>, String> {
        fs::create_dir_all(&staging_dir)
            .map_err(|e| format!("failed to create staging dir: {e}"))?;

        let mut digests = HashMap::new();
        for name in &expected_published {
            let content = rendered.get(name).cloned().unwrap_or_default();
            let staged_path = format!("{staging_dir}/{name}");
            harness_engine::atomic_io::write_atomic(Path::new(&staged_path), &content)
                .map_err(|e| format!("failed to stage '{name}': {e}"))?;
            digests.insert(name.to_string(), digest_str(&content));
        }

        // Ownership check BEFORE anything in the destination is touched.
        let previously_owned: HashSet<String> = read("publish-manifest.json")
            .and_then(|manifest| manifest["ownedFiles"].as_array().cloned())
            .unwrap_or_default()
            .into_iter()
            .filter_map(|item| item.as_str().map(String::from))
            .collect();
        let expected: HashSet<&str> = expected_published.iter().map(String::as_str).collect();
        if let Ok(entries) = fs::read_dir(DEST_DIR) {
            for entry in entries.flatten() {
                if entry.file_type().map(|t| t.is_file()).unwrap_or(false) {
                    let name = entry.file_name().to_string_lossy().to_string();
                    if !expected.contains(name.as_str()) {
                        return Err(format!(
                            "unrecognized file '{name}' exists in '{DEST_DIR}'; publish blocked."
                        ));
                    }
                    if !previously_owned.contains(&name) {
                        return Err(format!(
                            "file '{name}' exists in '{DEST_DIR}' but is not owned by a previous publish of this flow; publish blocked."
                        ));
                    }
                }
            }
        }

        // Validation passed: copy the four known files in, then write the manifest last.
        fs::create_dir_all(DEST_DIR).map_err(|e| format!("failed to create '{DEST_DIR}': {e}"))?;
        for name in &expected_published {
            let staged_path = format!("{staging_dir}/{name}");
            let dest_path = format!("{DEST_DIR}/{name}");
            fs::copy(&staged_path, &dest_path)
                .map_err(|e| format!("failed to publish '{name}': {e}"))?;
        }

        let manifest_digest = digest_str(
            &expected_published
                .iter()
                .map(|n| digests.get(n).cloned().unwrap_or_default())
                .collect::<Vec<_>>()
                .join("|"),
        );
        let manifest = json!({
            "ownedFiles": expected_published,
            "fileDigests": digests,
            "manifestDigest": manifest_digest,
            "publishedAt": chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Micros, false),
        });
        write("publish-manifest.json", &manifest);

        verify_postcondition(&digests)?;

        Ok(digests)
    })();

    let _ = fs::remove_dir_all(&staging_dir);

    result
}
