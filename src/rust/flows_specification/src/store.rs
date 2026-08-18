//! Namespaced store (mirrors `SpecificationStore.cs`): persistence and digest helpers
//! shared by every phase of the Specification flow.

use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{collections::HashSet, fs, path::Path};

pub(crate) const DIR: &str = ".harness/specification/active";
pub(crate) const SOURCES_FOLDER: &str = "specs/sources";

pub(crate) fn path(n: &str) -> String {
    format!("{DIR}/{n}")
}
pub(crate) fn read(n: &str) -> Option<Value> {
    serde_json::from_str(&fs::read_to_string(path(n)).ok()?).ok()
}
pub(crate) fn write(n: &str, v: &Value) {
    fs::create_dir_all(DIR).unwrap();
    harness_engine::atomic_io::write_atomic(
        Path::new(&path(n)),
        &serde_json::to_string(v).unwrap(),
    )
    .unwrap();
}
/// Digest of a JSON value's canonical (compact) serialization — used for parent-digest
/// freshness checks (ideaDigest, prdDigest, srsDigest, bundleDigest, ...).
pub(crate) fn digest(v: &Value) -> String {
    let mut h = Sha256::new();
    h.update(serde_json::to_vec(v).unwrap());
    format!("sha256:{:x}", h.finalize())
}
/// Digest of raw UTF-8 text — used for rendered Markdown content (never JSON-serialized),
/// mirroring `SpecificationPublisher.Digest(string content)`.
pub(crate) fn digest_str(content: &str) -> String {
    let mut h = Sha256::new();
    h.update(content.as_bytes());
    format!("sha256:{:x}", h.finalize())
}

pub(crate) fn run() -> Value {
    read("run.json")
        .unwrap_or(json!({"step":0,"status":"in_progress","phase":"start","counters":{}}))
}
/// Current harness step count (see `Harness.Engine.StateStore.Load().Step` in .NET) — synced
/// into run.json on every phase transition so `RunState.Step` reflects the real engine step,
/// not a locally-tracked counter.
fn current_step() -> i64 {
    harness_engine::state_store::load().step as i64
}
/// Normal phase transition: updates phase/status/step. Never touches `terminalReason` — a
/// non-terminal transition has nothing to report.
pub(crate) fn save(mut r: Value, p: &str, s: &str) {
    r["phase"] = json!(p);
    r["status"] = json!(s);
    r["step"] = json!(current_step());
    write("run.json", &r)
}
/// Terminal-status transition that does NOT change phase (mirrors the .NET `run with { Status
/// = ..., TerminalReason = ... }` pattern, which only overrides the fields listed). Records a
/// human-readable reason for `needs_human_decision`/`publish_blocked`.
pub(crate) fn save_status(mut r: Value, s: &str, reason: Option<&str>) {
    r["status"] = json!(s);
    r["step"] = json!(current_step());
    if let Some(reason) = reason {
        r["terminalReason"] = json!(reason);
    }
    write("run.json", &r)
}

pub(crate) fn proposal(p: &str) -> Option<Value> {
    read(&format!("{p}.proposal.json"))
}
pub(crate) fn accept(p: &str, v: &Value) {
    write(&format!("{p}.accepted.json"), v)
}
pub(crate) fn bundle_digest() -> String {
    let parts = ["idea", "prd", "srs", "sdd", "readiness"]
        .iter()
        .map(|phase| {
            read(&format!("{phase}.accepted.json")).map_or(String::new(), |value| digest(&value))
        })
        .collect::<Vec<_>>();
    let mut hasher = Sha256::new();
    hasher.update(parts.join("|"));
    format!("sha256:{:x}", hasher.finalize())
}

pub(crate) fn requirement_ids_of(srs: &Value) -> Vec<String> {
    srs["functionalRequirements"]
        .as_array()
        .into_iter()
        .flatten()
        .chain(srs["qualityRequirements"].as_array().into_iter().flatten())
        .filter_map(|item| item["id"].as_str().map(String::from))
        .collect()
}
pub(crate) fn adr_ids_of(sdd: &Value) -> Vec<String> {
    sdd["adrs"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|item| item["id"].as_str().map(String::from))
        .collect()
}

pub(crate) fn non_empty(value: &Value, key: &str) -> bool {
    value
        .get(key)
        .and_then(Value::as_array)
        .is_some_and(|items| !items.is_empty())
}
pub(crate) fn text_present(value: &Value, key: &str) -> bool {
    value
        .get(key)
        .and_then(Value::as_str)
        .is_some_and(|text| !text.trim().is_empty())
}
pub(crate) fn duplicate_ids(items: Option<&Vec<Value>>) -> bool {
    let mut ids = HashSet::new();
    items.is_some_and(|values| {
        values
            .iter()
            .filter_map(|item| item.get("id").and_then(Value::as_str))
            .any(|id| !ids.insert(id))
    })
}
