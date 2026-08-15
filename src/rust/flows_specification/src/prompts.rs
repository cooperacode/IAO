//! Prompts (mirrors `SpecificationTasks.Prompt.cs`): builds the driver-facing prompt text for
//! every phase, reading accepted documents/digests from `store` and the schema constants from
//! `evaluator` so the prompt text and the validation it primes never drift apart.

use crate::evaluator::{IDEA_SCHEMA, MAX_IDEA_UTF8_BYTES, PRD_SCHEMA, SDD_SCHEMA, SRS_SCHEMA};
use crate::store::{SOURCES_FOLDER, bundle_digest, digest, path, read};
use harness_engine::{Envelope, envelope_type};
use serde_json::Value;

const IDEA_SHAPE: &str = r#"{"schema":"iao/idea/v1","title":"...","problem":"...","users":["..."],"desiredOutcomes":["..."],"constraints":["..."],"openQuestions":[{"id":"OQ-1","question":"...","blocking":false}]}"#;
const PRD_SHAPE: &str = r#"{"schema":"iao/prd/v1","ideaDigest":"sha256:...","vision":"...","goals":[{"id":"G-1","statement":"..."}],"successMetrics":[{"id":"M-1","goalId":"G-1","measure":"...","target":"..."}],"nonGoals":["..."],"scope":["..."],"risks":[{"id":"R-1","description":"...","mitigation":"...","severity":"..."}],"decisions":[{"id":"D-1","statement":"...","rationale":"..."}],"openQuestions":[]}"#;
const SRS_SHAPE: &str = r#"{"schema":"iao/srs/v1","prdDigest":"sha256:...","functionalRequirements":[{"id":"RF-1","goalIds":["G-1"],"statement":"...","dependsOn":[],"acceptanceIds":["AC-1"]}],"qualityRequirements":[],"acceptanceCriteria":[{"id":"AC-1","requirementIds":["RF-1"],"given":"...","when":"...","then":"..."}],"interfaces":[],"dataRules":[],"delivery":{"target":"...","verificationStrategy":"...","isBootstrap":true}}"#;
const SDD_SHAPE: &str = r#"{"schema":"iao/sdd/v1","srsDigest":"sha256:...","adrs":[{"id":"ADR-1","title":"...","decision":"...","rationale":"...","requirementIds":["RF-1"]}],"controls":[{"id":"IC-1","name":"...","description":"...","requirementIds":["RF-1"]}]}"#;
const REVIEW_SHAPE: &str = r#"{"verdict":"READY","slices":[{"id":"SL-1","classification":"...","goal":"...","inScope":["..."],"outOfScope":["..."],"observableOutcome":"...","requirementIds":["RF-1"],"adrIds":["ADR-1"],"dependsOn":[],"contracts":["..."],"happyPath":"...","failurePath":"...","acceptanceCriterion":"...","suggestedTarget":"...","suggestedVerificationStrategy":"..."}],"conflicts":[],"residuals":[]}"#;
const APPROVAL_SHAPE: &str = r#"{"decision":"approved","bundleDigest":"sha256:...","rationale":"...","approvedBy":"...","decidedAt":"2026-01-01T00:00:00Z"}"#;

fn violations_list(violations: &[String]) -> String {
    violations.iter().map(|v| format!("- {v}")).collect::<Vec<_>>().join("\n")
}

fn command_prompt(input: &str, command: &str, skill: &str) -> String {
    harness_engine::prompt_formatter::format(
        input,
        &Envelope::new(envelope_type::COMMAND, command, vec![]),
        Some(&harness_engine::prompt_formatter::skills(&[skill])),
    )
}

/// Reattaches the ingested source material (written once, in `start`) on every discover/retry
/// turn — a driver that already dropped it from context must not fall back to inventing an
/// unrelated idea just because this is a retry. Mirrors `SourcesBlock()`.
fn sources_block() -> String {
    let sources = read("sources.accepted.json");
    let files: Vec<String> = sources
        .as_ref()
        .and_then(|s| s["files"].as_array())
        .into_iter()
        .flatten()
        .filter_map(|f| f.as_str().map(String::from))
        .collect();
    if files.is_empty() {
        "No sources folder was found (or it was empty). Ask the human operator for the idea, problem, users and constraints in this conversation, then frame it below.\n".to_string()
    } else {
        let content = sources.as_ref().and_then(|s| s["content"].as_str()).unwrap_or("");
        format!(
            "<sources folder=\"{SOURCES_FOLDER}\" files=\"{}\">{}</sources>\n",
            files.join(", "),
            harness_engine::prompt_formatter::inline(content)
        )
    }
}

pub(crate) fn discover_prompt() -> String {
    let input = format!(
        "Frame the idea for this Specification run (blueprint 0004 §2/§3, discover phase).\n\n\
{}\n\
Ground the idea in the material above when present — do not invent facts it doesn't support, \
and prefer citing/summarizing it over guessing. Write a JSON OBJECT to the file '{}' (a real \
file, written with your file-write tool — NOT escaped or embedded inside the envelope you send \
back) with this shape: {IDEA_SHAPE}\n\
`schema` must be exactly \"{IDEA_SCHEMA}\". Provide a title, a problem statement, at least one \
user and one desired outcome; keep the canonical JSON under {MAX_IDEA_UTF8_BYTES} UTF-8 bytes \
and give every open question a unique id.\n\n\
Return `discover` without arguments when done; the harness will validate the file and either \
advance to `product` or re-request `discover` with the reported violations.",
        sources_block(),
        path("idea.proposal.json")
    );
    command_prompt(&input, "discover", "spec-discovery")
}
pub(crate) fn discover_retry_prompt(violations: &[String]) -> String {
    let input = format!(
        "{}\n\
The idea proposal at '{}' did not pass IdeaEvaluator:\n{}\n\n\
Rewrite the file at the exact same path with this shape: {IDEA_SHAPE}\n\
`schema` must be exactly \"{IDEA_SCHEMA}\". Return `discover` without arguments for another \
harness-controlled attempt.",
        sources_block(),
        path("idea.proposal.json"),
        violations_list(violations)
    );
    command_prompt(&input, "discover", "spec-discovery")
}

pub(crate) fn product_prompt() -> String {
    let idea_digest = read("idea.accepted.json").as_ref().map(digest).unwrap_or_default();
    let input = format!(
        "Draft the PRD for this Specification run (blueprint 0004 §2/§3, product phase), \
building on the accepted idea (digest '{idea_digest}').\n\n\
Write a JSON OBJECT to the file '{}' (a real file, written with your file-write tool — NOT \
escaped or embedded inside the envelope you send back) with this shape: {PRD_SHAPE}\n\
`schema` must be exactly \"{PRD_SCHEMA}\" and `ideaDigest` must be set to exactly \
'{idea_digest}'. Provide at least one goal, one non-goal, one scope entry and one success \
metric with a measure and target that are both present and distinct; leave no blocking open \
question unresolved.\n\n\
Return `product` without arguments when done; the harness will validate the file and either \
advance to `analysis`, or re-request `product` with the reported violations.",
        path("prd.proposal.json")
    );
    command_prompt(&input, "product", "spec-product")
}
pub(crate) fn product_retry_prompt(violations: &[String]) -> String {
    let idea_digest = read("idea.accepted.json").as_ref().map(digest).unwrap_or_default();
    let input = format!(
        "The PRD proposal at '{}' did not pass PrdEvaluator:\n{}\n\n\
Rewrite the file at the exact same path with this shape: {PRD_SHAPE}\n\
`schema` must be exactly \"{PRD_SCHEMA}\" and `ideaDigest` must be set to exactly \
'{idea_digest}'. Return `product` without arguments for another harness-controlled attempt.",
        path("prd.proposal.json"),
        violations_list(violations)
    );
    command_prompt(&input, "product", "spec-product")
}

pub(crate) fn analysis_prompt() -> String {
    let prd_digest = read("prd.accepted.json").as_ref().map(digest).unwrap_or_default();
    let input = format!(
        "Draft the SRS for this Specification run (blueprint 0004 §2/§3, analysis phase), \
building on the accepted PRD (digest '{prd_digest}').\n\n\
Write a JSON OBJECT to the file '{}' (a real file, written with your file-write tool — NOT \
escaped or embedded inside the envelope you send back) with this shape: {SRS_SHAPE}\n\
`schema` must be exactly \"{SRS_SCHEMA}\" and `prdDigest` must be set to exactly '{prd_digest}'. \
Every goal from the accepted PRD must be covered by at least one requirement's `goalIds`. Every \
functional and quality requirement needs a unique id and at least one `acceptanceIds` entry \
that resolves to a real entry in `acceptanceCriteria`. Every `dependsOn` id and every \
acceptance criterion/interface/data-rule requirement reference must point at a requirement id \
that actually exists.\n\n\
Return `analysis` without arguments when done; the harness will validate the file and either \
advance to `design`, or re-request `analysis` with the reported violations.",
        path("srs.proposal.json")
    );
    command_prompt(&input, "analysis", "spec-analysis")
}
pub(crate) fn analysis_retry_prompt(violations: &[String]) -> String {
    let prd_digest = read("prd.accepted.json").as_ref().map(digest).unwrap_or_default();
    let input = format!(
        "The SRS proposal at '{}' did not pass SrsEvaluator:\n{}\n\n\
Rewrite the file at the exact same path with this shape: {SRS_SHAPE}\n\
`schema` must be exactly \"{SRS_SCHEMA}\" and `prdDigest` must be set to exactly '{prd_digest}'. \
Return `analysis` without arguments for another harness-controlled attempt.",
        path("srs.proposal.json"),
        violations_list(violations)
    );
    command_prompt(&input, "analysis", "spec-analysis")
}

pub(crate) fn design_prompt() -> String {
    let srs_digest = read("srs.accepted.json").as_ref().map(digest).unwrap_or_default();
    let input = format!(
        "Draft the SDD for this Specification run (blueprint 0004 §2/§3, design phase), \
building on the accepted SRS (digest '{srs_digest}').\n\n\
Write a JSON OBJECT to the file '{}' (a real file, written with your file-write tool — NOT \
escaped or embedded inside the envelope you send back) with this shape: {SDD_SHAPE}\n\
`schema` must be exactly \"{SDD_SCHEMA}\" and `srsDigest` must be set to exactly '{srs_digest}'. \
Every functional and quality requirement from the accepted SRS must be allocated to \
(referenced by) at least one ADR's `requirementIds`. Every ADR id must be unique, and every \
ADR/control requirement reference must point at a requirement id that actually exists in the \
accepted SRS.\n\n\
Return `design` without arguments when done; the harness will validate the file and either \
persist sdd.accepted.json and advance to `review`, or re-request `design` with the reported \
violations.",
        path("sdd.proposal.json")
    );
    command_prompt(&input, "design", "spec-design")
}
pub(crate) fn design_retry_prompt(violations: &[String]) -> String {
    let srs_digest = read("srs.accepted.json").as_ref().map(digest).unwrap_or_default();
    let input = format!(
        "The SDD proposal at '{}' did not pass SddEvaluator:\n{}\n\n\
Rewrite the file at the exact same path with this shape: {SDD_SHAPE}\n\
`schema` must be exactly \"{SDD_SCHEMA}\" and `srsDigest` must be set to exactly '{srs_digest}'. \
Return `design` without arguments for another harness-controlled attempt.",
        path("sdd.proposal.json"),
        violations_list(violations)
    );
    command_prompt(&input, "design", "spec-design")
}

pub(crate) fn review_prompt() -> String {
    let input = format!(
        "Assess readiness for this Specification run (blueprint 0004 §2/§7, blueprint 0006 \
review phase): judge whether the accepted idea/PRD/SRS/SDD chain is internally consistent, or \
whether an earlier phase needs to be redone.\n\n\
Write a JSON OBJECT to the file '{}' (a real file, written with your file-write tool — NOT \
escaped or embedded inside the envelope you send back) with this shape: {REVIEW_SHAPE}\n\
`verdict` must be exactly one of \"READY\", \"FAIL:product\", \"FAIL:analysis\" or \
\"FAIL:design\". `conflicts` and `residuals` are always required arrays (use `[]` when there \
are none — never omit them).\n\n\
If `verdict` is \"READY\": propose at most 10 readiness slices, each with a unique id. Every \
`requirementIds` entry must reference a real requirement from the accepted SRS and every \
`adrIds` entry must reference a real ADR from the accepted SDD. Every `dependsOn` entry must \
name another slice in this same list (never itself, never an id outside this proposal); the \
dependency graph must be acyclic, and at least one slice must have an empty `dependsOn` (a \
starting slice). Every requirement in the accepted SRS must be covered by at least one slice's \
`requirementIds` — the slices must form a complete cover.\n\n\
If `verdict` starts with \"FAIL:\": leave `slices` empty — a FAIL verdict is a rejection of an \
earlier phase, not a slice proposal; explain the rejection through `conflicts`/`residuals` \
instead.\n\n\
Return `review` without arguments when done; the harness will validate the file and either \
pause for approval (READY), recascade to the failing phase (FAIL:*), or re-request `review` \
with the reported violations.",
        path("review.proposal.json")
    );
    command_prompt(&input, "review", "spec-review")
}
pub(crate) fn review_retry_prompt(violations: &[String]) -> String {
    let input = format!(
        "The readiness verdict proposal at '{}' did not pass ReadinessEvaluator:\n{}\n\n\
Rewrite the file at the exact same path with this shape: {REVIEW_SHAPE}\n\
`verdict` must be exactly one of \"READY\", \"FAIL:product\", \"FAIL:analysis\" or \
\"FAIL:design\", and `conflicts`/`residuals` must always be present arrays. Return `review` \
without arguments for another harness-controlled attempt.",
        path("review.proposal.json"),
        violations_list(violations)
    );
    command_prompt(&input, "review", "spec-review")
}

fn bundle_preview() -> String {
    let prd = read("prd.accepted.json");
    let srs = read("srs.accepted.json");
    let sdd = read("sdd.accepted.json");
    let readiness = read("readiness.accepted.json");
    let count = |v: &Option<Value>, key: &str| -> usize {
        v.as_ref().and_then(|d| d[key].as_array()).map(|a| a.len()).unwrap_or(0)
    };
    format!(
        "## Preview — bundle to be published to specs/active/\n\n\
- **PRD vision:** {} — {} goal(s), {} success metric(s)\n\
- **SRS:** {} functional + {} quality requirement(s), {} acceptance criteria\n\
- **SDD:** {} ADR(s), {} control(s)\n\
- **Readiness:** verdict '{}', {} slice(s)\n",
        prd.as_ref().and_then(|p| p["vision"].as_str()).unwrap_or("(missing)"),
        count(&prd, "goals"),
        count(&prd, "successMetrics"),
        count(&srs, "functionalRequirements"),
        count(&srs, "qualityRequirements"),
        count(&srs, "acceptanceCriteria"),
        count(&sdd, "adrs"),
        count(&sdd, "controls"),
        readiness.as_ref().and_then(|r| r["verdict"].as_str()).unwrap_or("(missing)"),
        count(&readiness, "slices"),
    )
}

pub(crate) fn approve_prompt() -> String {
    let bundle_digest = bundle_digest();
    let input = format!(
        "Review the bundle for this Specification run before publication (blueprint 0004 §5 \
ApprovalEvaluator, §6 Publicação segura).\n\n\
{}\n\
The current bundle digest is '{bundle_digest}'.\n\n\
Write a JSON OBJECT to the file '{}' (a real file, written with your file-write tool — NOT \
escaped or embedded inside the envelope you send back) with this shape: {APPROVAL_SHAPE}\n\
`decision` must be exactly \"approved\" or \"revise\". `bundleDigest` must be set to exactly \
'{bundle_digest}' — it is re-checked against the CURRENT accepted chain at evaluation time, so \
if any accepted document changes after this preview, resend with the freshly reported digest \
instead of the one shown here. `rationale` must state a real reason, not a placeholder.\n\n\
If `decision` is \"approved\": the four accepted documents (PRD, SRS, SDD, readiness) are \
rendered and published to 'specs/active/' as 00-prd.md, \
10-software-requirements-specification.md, 20-software-design-document.md and \
30-readiness-handoff.md, and the run completes.\n\n\
If `decision` is \"revise\": the run routes back to the review phase so a fresh readiness \
verdict — including a FAIL:* one, if a deeper phase needs rework — can be issued.\n\n\
Return `approve` without arguments when done; the harness will validate the file and act on the \
decision, or re-request `approve` with the reported violations.",
        bundle_preview(),
        path("approval.proposal.json")
    );
    command_prompt(&input, "approve", "spec-review")
}
pub(crate) fn approve_retry_prompt(violations: &[String]) -> String {
    let bundle_digest = bundle_digest();
    let input = format!(
        "The approval proposal at '{}' did not pass ApprovalEvaluator:\n{}\n\n\
The current bundle digest is '{bundle_digest}'. Rewrite the file at the exact same path with \
this shape: {APPROVAL_SHAPE}\n\
`decision` must be exactly \"approved\" or \"revise\", `bundleDigest` must be set to exactly \
'{bundle_digest}', and `rationale` must state a real reason. Return `approve` without \
arguments for another harness-controlled attempt.",
        path("approval.proposal.json"),
        violations_list(violations)
    );
    command_prompt(&input, "approve", "spec-review")
}
