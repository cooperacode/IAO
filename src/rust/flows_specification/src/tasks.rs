//! Tasks (mirrors `SpecificationTasks.cs`): the state machine driving one phase transition
//! per call, wiring `store` (persistence), `evaluator` (validation), `renderer`/`publisher`
//! (publication) and `prompts` (driver-facing text) together.

use crate::evaluator::{validate_approval, validate_development_readiness, validate_idea, validate_prd, validate_readiness, validate_sdd, validate_srs};
use crate::prompts::{
    analysis_prompt, analysis_retry_prompt, approve_prompt, approve_retry_prompt, design_prompt,
    design_retry_prompt, discover_prompt, discover_retry_prompt, product_prompt,
    product_retry_prompt, review_prompt, review_retry_prompt,
};
use crate::publisher::publish;
use crate::renderer::render_all;
use crate::store::{
    DIR, SOURCES_FOLDER, accept, adr_ids_of, bundle_digest, digest, path, proposal, read,
    requirement_ids_of, run, save, save_status, write,
};
use harness_engine::Envelope;
use serde_json::json;
use std::fs;

pub(crate) const STEP_BUDGET: i32 = 18;

pub(crate) fn start() -> String {
    let r = run();
    let s = r["status"].as_str().unwrap_or("");
    let p = r["phase"].as_str().unwrap_or("");
    let resumable = (s == "in_progress"
        && ["discover", "product", "analysis", "design", "review"].contains(&p))
        || (s == "awaiting_approval" && p == "approve");
    if resumable {
        return match p {
            "product" => product_prompt(),
            "analysis" => analysis_prompt(),
            "design" => design_prompt(),
            "review" => review_prompt(),
            "approve" => approve_prompt(),
            _ => discover_prompt(),
        };
    }
    let _ = fs::remove_dir_all(DIR);
    write(
        "run.json",
        &json!({"step":0,"status":"in_progress","phase":"discover","counters":{}}),
    );
    if harness_engine::docs_reader::has_docs(SOURCES_FOLDER) {
        let (content, files) = harness_engine::docs_reader::read(SOURCES_FOLDER);
        accept("sources", &json!({"files": files, "content": content}));
    }
    discover_prompt()
}

pub(crate) fn discover(_: Option<&Envelope>) -> String {
    let Some(v) = proposal("idea") else {
        return discover_retry_prompt(&[format!(
            "no readable idea proposal was found at '{}' (missing or not valid JSON).",
            path("idea.proposal.json")
        )]);
    };
    let sources = read("sources.accepted.json");
    let files: Vec<String> = sources
        .as_ref()
        .and_then(|s| s["files"].as_array())
        .into_iter()
        .flatten()
        .filter_map(|f| f.as_str().map(String::from))
        .collect();
    let source = if !files.is_empty() {
        format!("{SOURCES_FOLDER} ({} file(s)): {}", files.len(), files.join(", "))
    } else {
        "driver".to_string()
    };
    let source_digest = match &sources {
        Some(s) => digest(s),
        None => digest(&v),
    };
    let errors = validate_idea(&v, &source, &source_digest);
    if !errors.is_empty() {
        return discover_retry_prompt(&errors);
    }
    accept("idea", &v);
    save(run(), "product", "in_progress");
    product_prompt()
}

pub(crate) fn product(_: Option<&Envelope>) -> String {
    let Some(v) = proposal("prd") else {
        return product_retry_prompt(&[format!(
            "no readable PRD proposal was found at '{}' (missing or not valid JSON).",
            path("prd.proposal.json")
        )]);
    };
    let Some(i) = read("idea.accepted.json") else {
        return product_retry_prompt(&["missing accepted idea.".to_string()]);
    };
    let errors = validate_prd(&v, &digest(&i));
    if !errors.is_empty() {
        return product_retry_prompt(&errors);
    }
    accept("prd", &v);
    save(run(), "analysis", "in_progress");
    analysis_prompt()
}

pub(crate) fn analysis(_: Option<&Envelope>) -> String {
    let Some(v) = proposal("srs") else {
        return analysis_retry_prompt(&[format!(
            "no readable SRS proposal was found at '{}' (missing or not valid JSON).",
            path("srs.proposal.json")
        )]);
    };
    let Some(p) = read("prd.accepted.json") else {
        return analysis_retry_prompt(&["missing accepted PRD.".to_string()]);
    };
    let goals = p["goals"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|goal| goal["id"].as_str().map(String::from))
        .collect::<Vec<_>>();
    let errors = validate_srs(&v, &digest(&p), &goals);
    if !errors.is_empty() {
        return analysis_retry_prompt(&errors);
    }
    accept("srs", &v);
    save(run(), "design", "in_progress");
    design_prompt()
}

pub(crate) fn design(_: Option<&Envelope>) -> String {
    let Some(v) = proposal("sdd") else {
        return design_retry_prompt(&[format!(
            "no readable SDD proposal was found at '{}' (missing or not valid JSON).",
            path("sdd.proposal.json")
        )]);
    };
    let Some(s) = read("srs.accepted.json") else {
        return design_retry_prompt(&["missing accepted SRS.".to_string()]);
    };
    let requirements = requirement_ids_of(&s);
    let errors = validate_sdd(&v, &digest(&s), &requirements);
    if !errors.is_empty() {
        return design_retry_prompt(&errors);
    }
    accept("sdd", &v);
    save(run(), "review", "in_progress");
    review_prompt()
}

pub(crate) fn review(_: Option<&Envelope>) -> String {
    let Some(v) = proposal("review") else {
        return review_retry_prompt(&[format!(
            "no readable readiness verdict proposal was found at '{}' (missing or not valid JSON).",
            path("review.proposal.json")
        )]);
    };
    let verdict = v["verdict"].as_str().unwrap_or("");
    let srs = read("srs.accepted.json").unwrap_or_default();
    let sdd = read("sdd.accepted.json").unwrap_or_default();
    let requirements = requirement_ids_of(&srs);
    let adrs = adr_ids_of(&sdd);
    let errors = validate_readiness(&v, &requirements, &adrs);
    if !errors.is_empty() {
        return review_retry_prompt(&errors);
    }
    if verdict == "READY" {
        accept("readiness", &v);
        save(run(), "approve", "awaiting_approval");
        return "stop".into();
    }
    let mut r = run();
    let current_count = r["counters"]["recascades"].as_i64().unwrap_or(0);
    if current_count >= 2 {
        save_status(r, "needs_human_decision", Some("recascade limit reached"));
        return "stop".into();
    }
    r["counters"]["recascades"] = json!(current_count + 1);
    let target = verdict.split(':').nth(1).unwrap_or("review");
    save(r, target, "in_progress");
    match target {
        "product" => product_prompt(),
        "analysis" => analysis_prompt(),
        "design" => design_prompt(),
        _ => review_retry_prompt(&[format!("READINESS_VERDICT_INVALID: unroutable verdict '{verdict}'")]),
    }
}

pub(crate) fn approve(_: Option<&Envelope>) -> String {
    let Some(v) = proposal("approval") else {
        return approve_retry_prompt(&[format!(
            "no readable approval proposal was found at '{}' (missing or not valid JSON).",
            path("approval.proposal.json")
        )]);
    };
    let current = bundle_digest();
    let errors = validate_approval(&v, &current);
    if !errors.is_empty() {
        return approve_retry_prompt(&errors);
    }

    let run_state = run();
    let decision = v["decision"].as_str().unwrap_or("");

    if decision == "revise" {
        save(run_state, "review", "in_progress");
        return review_prompt();
    }

    // decision == "approved" (validate_approval already restricted the allowed set).
    let prd = read("prd.accepted.json");
    let srs = read("srs.accepted.json");
    let sdd = read("sdd.accepted.json");
    let readiness = read("readiness.accepted.json");
    let (Some(prd), Some(srs), Some(sdd), Some(readiness)) = (prd, srs, sdd, readiness) else {
        save_status(
            run_state,
            "publish_blocked",
            Some("one or more accepted documents (prd/srs/sdd/readiness) are missing; cannot publish."),
        );
        return "stop".into();
    };

    // Pre-publish gate (blueprint 0006 "Antes de promover, o DevelopmentReadinessEvaluator
    // deve provar..."): rendered via the SAME render_all() a real publish() call uses, so this
    // byte-budget check sees exactly what would be written. specs/active/ is not touched if
    // this fails.
    let requirement_ids = requirement_ids_of(&srs);
    let adr_ids = adr_ids_of(&sdd);
    let rendered = render_all(&prd, &srs, &sdd, &readiness);
    let docs_max_chars = harness_engine::harness_config::current().docs_max_chars as i64;
    let bundle_digest_at_approval = v["bundleDigest"].as_str().unwrap_or("");
    let gate_errors = validate_development_readiness(
        &prd,
        &readiness,
        &requirement_ids,
        &adr_ids,
        bundle_digest_at_approval,
        &bundle_digest(),
        &rendered,
        docs_max_chars,
    );
    if !gate_errors.is_empty() {
        let reason = gate_errors.join("; ");
        save_status(run_state, "publish_blocked", Some(&reason));
        return "stop".into();
    }

    match publish(&prd, &srs, &sdd, &readiness) {
        Ok(_) => {
            save(run_state, "stop", "completed");
            "stop".into()
        }
        Err(reason) => {
            save_status(run_state, "publish_blocked", Some(&reason));
            "stop".into()
        }
    }
}
