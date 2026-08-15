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
pub(crate) fn publish(prd: &Value, srs: &Value, sdd: &Value, readiness: &Value) -> Result<HashMap<String, String>, String> {
    let rendered = render_all(prd, srs, sdd, readiness);
    let staging_dir = format!("{DEST_DIR}.staging-{}", random_hex());

    let result = (|| -> Result<HashMap<String, String>, String> {
        fs::create_dir_all(&staging_dir).map_err(|e| format!("failed to create staging dir: {e}"))?;

        let mut digests = HashMap::new();
        for name in FILENAMES {
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
        let expected: HashSet<&str> = FILENAMES.iter().copied().collect();
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
        for name in FILENAMES {
            let staged_path = format!("{staging_dir}/{name}");
            let dest_path = format!("{DEST_DIR}/{name}");
            fs::copy(&staged_path, &dest_path).map_err(|e| format!("failed to publish '{name}': {e}"))?;
        }

        let manifest_digest = digest_str(
            &FILENAMES
                .iter()
                .map(|n| digests.get(*n).cloned().unwrap_or_default())
                .collect::<Vec<_>>()
                .join("|"),
        );
        let manifest = json!({
            "ownedFiles": FILENAMES,
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
