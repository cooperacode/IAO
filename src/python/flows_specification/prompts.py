"""Builds the Specification flow's prompts — the "strategy" kept separate from the state
machine in `tasks.py` (same split as `flows_development/prompts.py`, and as the .NET port's
`SpecificationTasks.Prompt.cs` partial class alongside `SpecificationTasks.cs`).
"""

from __future__ import annotations

from harness_engine import prompt_formatter
from harness_engine.envelope import Envelope, EnvelopeType

from . import store

# Known-convention paths under the store's namespaced directory (blueprint 0004 §4).
# store.py keeps its proposal/accepted path builders private; the prompts only need to tell
# the driver where to write, so this mirrors the documented convention directly.
IDEA_PROPOSAL_PATH = ".harness/specification/active/idea.proposal.json"
PRD_PROPOSAL_PATH = ".harness/specification/active/prd.proposal.json"
SRS_PROPOSAL_PATH = ".harness/specification/active/srs.proposal.json"
SDD_PROPOSAL_PATH = ".harness/specification/active/sdd.proposal.json"
REVIEW_PROPOSAL_PATH = ".harness/specification/active/review.proposal.json"
APPROVAL_PROPOSAL_PATH = ".harness/specification/active/approval.proposal.json"

# Repo-relative input directory for `start`'s ingested source material — mirrors
# SpecificationTasks.SourcesFolder in the .NET port.
SOURCES_FOLDER = "specs/sources"

# Kept in sync with evaluator.MAX_IDEA_UTF8_BYTES / the *_SCHEMA literals evaluator.py checks
# against — repeated here (not imported) because the .NET reference keeps the same duplication
# between SpecificationEvaluator's constants and the prompt text that quotes them.
MAX_IDEA_UTF8_BYTES = 20_000
IDEA_SCHEMA = "iao/idea/v1"
PRD_SCHEMA = "iao/prd/v1"
SRS_SCHEMA = "iao/srs/v1"
SDD_SCHEMA = "iao/sdd/v1"

IDEA_SHAPE = (
    '{"schema":"iao/idea/v1","title":"...","problem":"...","users":["..."],'
    '"desiredOutcomes":["..."],"constraints":["..."],'
    '"openQuestions":[{"id":"OQ-1","question":"...","blocking":false}]}'
)
PRD_SHAPE = (
    '{"schema":"iao/prd/v1","ideaDigest":"sha256:...","vision":"...",'
    '"goals":[{"id":"G-1","statement":"..."}],'
    '"successMetrics":[{"id":"M-1","goalId":"G-1","measure":"...","target":"..."}],'
    '"nonGoals":["..."],"scope":["..."],'
    '"risks":[{"id":"R-1","description":"...","mitigation":"...","severity":"..."}],'
    '"decisions":[{"id":"D-1","statement":"...","rationale":"..."}],"openQuestions":[]}'
)
SRS_SHAPE = (
    '{"schema":"iao/srs/v1","prdDigest":"sha256:...",'
    '"functionalRequirements":[{"id":"RF-1","goalIds":["G-1"],"statement":"...",'
    '"dependsOn":[],"acceptanceIds":["AC-1"]}],"qualityRequirements":[],'
    '"acceptanceCriteria":[{"id":"AC-1","requirementIds":["RF-1"],"given":"...",'
    '"when":"...","then":"..."}],"interfaces":[],"dataRules":[],'
    '"delivery":{"target":"...","verificationStrategy":"...","isBootstrap":true}}'
)
SDD_SHAPE = (
    '{"schema":"iao/sdd/v1","srsDigest":"sha256:...",'
    '"adrs":[{"id":"ADR-1","title":"...","decision":"...","rationale":"...",'
    '"requirementIds":["RF-1"]}],'
    '"controls":[{"id":"IC-1","name":"...","description":"...","requirementIds":["RF-1"]}]}'
)
REVIEW_SHAPE = (
    '{"verdict":"READY","slices":[{"id":"SL-1","classification":"...","goal":"...",'
    '"inScope":["..."],"outOfScope":["..."],"observableOutcome":"...",'
    '"requirementIds":["RF-1"],"adrIds":["ADR-1"],"dependsOn":[],"contracts":["..."],'
    '"happyPath":"...","failurePath":"...","acceptanceCriterion":"...",'
    '"suggestedTarget":"...","suggestedVerificationStrategy":"..."}],'
    '"conflicts":[],"residuals":[]}'
)
APPROVAL_SHAPE = (
    '{"decision":"approved","bundleDigest":"sha256:...","rationale":"...",'
    '"approvedBy":"...","decidedAt":"2026-01-01T00:00:00Z"}'
)


def _violation_lines(violations) -> str:
    return "\n".join(f"- {v}" for v in violations)


# Ingested once at `start` (tasks.start reads/writes the "sources" phase) and reattached on
# every discover/retry turn — a driver that already dropped the source material from its
# context must not fall back to inventing an unrelated idea just because this is a retry, not
# the first turn. Mirrors SpecificationTasks.Prompt.cs's SourcesBlock().
def _sources_block() -> str:
    sources, _ = store.read_accepted("sources")
    files = (sources or {}).get("files") or []
    if not files:
        return (
            "No sources folder was found (or it was empty). Ask the human operator for "
            "the idea, problem, users and constraints in this conversation, then frame it "
            "below."
        )
    content = (sources or {}).get("content", "")
    return (
        f'<sources folder="{SOURCES_FOLDER}" files="{", ".join(files)}">'
        f"{prompt_formatter.inline(content)}</sources>\n"
    )


# Mirrors SpecificationTasks.Prompt.cs's BundlePreview() — a preview of the bundle about to be
# published, shown on the approve prompt. Falls back to "(missing)"/0 the same way the .NET
# port's null-conditional operators do when a phase isn't accepted yet.
def _bundle_preview() -> str:
    prd, _ = store.read_accepted("prd")
    srs, _ = store.read_accepted("srs")
    sdd, _ = store.read_accepted("sdd")
    readiness, _ = store.read_accepted("readiness")

    vision = prd.get("vision") if prd else None
    goals = len(prd.get("goals") or []) if prd else 0
    metrics = len(prd.get("successMetrics") or []) if prd else 0

    functional = len(srs.get("functionalRequirements") or []) if srs else 0
    quality = len(srs.get("qualityRequirements") or []) if srs else 0
    acceptance = len(srs.get("acceptanceCriteria") or []) if srs else 0

    adrs = len(sdd.get("adrs") or []) if sdd else 0
    controls = len(sdd.get("controls") or []) if sdd else 0

    verdict = readiness.get("verdict") if readiness else None
    slices = len(readiness.get("slices") or []) if readiness else 0

    return f"""## Preview — bundle to be published to specs/active/

- **PRD vision:** {vision if vision is not None else "(missing)"} — {goals} goal(s), {metrics} success metric(s)
- **SRS:** {functional} functional + {quality} quality requirement(s), {acceptance} acceptance criteria
- **SDD:** {adrs} ADR(s), {controls} control(s)
- **Readiness:** verdict '{verdict if verdict is not None else "(missing)"}', {slices} slice(s)"""


# --- discover -----------------------------------------------------------------------------


def discover_prompt() -> str:
    input_text = f"""Frame the idea for this Specification run (blueprint 0004 §2/§3, discover phase).

{_sources_block()}Ground the idea in the material above when present — do not invent
facts it doesn't support, and prefer citing/summarizing it over guessing. Write a
JSON OBJECT to the file '{IDEA_PROPOSAL_PATH}' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: {IDEA_SHAPE}
`schema` must be exactly "{IDEA_SCHEMA}". Provide a title, a
problem statement, at least one user and one desired outcome; keep the canonical JSON
under {MAX_IDEA_UTF8_BYTES} UTF-8 bytes and give every open
question a unique id.

Return `discover` without arguments when done; the harness will validate the file and
either advance to `product` or re-request `discover` with the reported violations."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "discover", ()),
        prompt_formatter.skills("spec-discovery"),
    )


def discover_retry_prompt(violations) -> str:
    input_text = f"""{_sources_block()}The idea proposal at '{IDEA_PROPOSAL_PATH}' did not pass IdeaEvaluator:
{_violation_lines(violations)}

Rewrite the file at the exact same path with this shape: {IDEA_SHAPE}
`schema` must be exactly "{IDEA_SCHEMA}". Return `discover`
without arguments for another harness-controlled attempt."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "discover", ()),
        prompt_formatter.skills("spec-discovery"),
    )


# --- product --------------------------------------------------------------------------------


def product_prompt() -> str:
    _, idea_digest = store.read_accepted("idea")
    input_text = f"""Draft the PRD for this Specification run (blueprint 0004 §2/§3, product phase),
building on the accepted idea (digest '{idea_digest}').

Write a JSON OBJECT to the file '{PRD_PROPOSAL_PATH}' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: {PRD_SHAPE}
`schema` must be exactly "{PRD_SCHEMA}" and `ideaDigest` must be
set to exactly '{idea_digest}'. Provide at least one goal, one non-goal, one scope
entry and one success metric with a measure and target that are both present and
distinct; leave no blocking open question unresolved.

Return `product` without arguments when done; the harness will validate the file and
either advance to `analysis`, or re-request `product` with the reported violations."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "product", ()),
        prompt_formatter.skills("spec-product"),
    )


def product_retry_prompt(violations) -> str:
    _, idea_digest = store.read_accepted("idea")
    input_text = f"""The PRD proposal at '{PRD_PROPOSAL_PATH}' did not pass PrdEvaluator:
{_violation_lines(violations)}

Rewrite the file at the exact same path with this shape: {PRD_SHAPE}
`schema` must be exactly "{PRD_SCHEMA}" and `ideaDigest` must be
set to exactly '{idea_digest}'. Return `product` without arguments for another
harness-controlled attempt."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "product", ()),
        prompt_formatter.skills("spec-product"),
    )


# --- analysis -------------------------------------------------------------------------------


def analysis_prompt() -> str:
    _, prd_digest = store.read_accepted("prd")
    input_text = f"""Draft the SRS for this Specification run (blueprint 0004 §2/§3, analysis phase),
building on the accepted PRD (digest '{prd_digest}').

Write a JSON OBJECT to the file '{SRS_PROPOSAL_PATH}' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: {SRS_SHAPE}
`schema` must be exactly "{SRS_SCHEMA}" and `prdDigest` must be
set to exactly '{prd_digest}'. Every goal from the accepted PRD must be covered by at
least one requirement's `goalIds`. Every functional and quality requirement needs a
unique id and at least one `acceptanceIds` entry that resolves to a real entry in
`acceptanceCriteria`. Every `dependsOn` id and every acceptance criterion/interface/
data-rule requirement reference must point at a requirement id that actually exists.

Return `analysis` without arguments when done; the harness will validate the file and
either advance to `design`, or re-request `analysis` with the reported violations."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "analysis", ()),
        prompt_formatter.skills("spec-analysis"),
    )


def analysis_retry_prompt(violations) -> str:
    _, prd_digest = store.read_accepted("prd")
    input_text = f"""The SRS proposal at '{SRS_PROPOSAL_PATH}' did not pass SrsEvaluator:
{_violation_lines(violations)}

Rewrite the file at the exact same path with this shape: {SRS_SHAPE}
`schema` must be exactly "{SRS_SCHEMA}" and `prdDigest` must be
set to exactly '{prd_digest}'. Return `analysis` without arguments for another
harness-controlled attempt."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "analysis", ()),
        prompt_formatter.skills("spec-analysis"),
    )


# --- design ---------------------------------------------------------------------------------


def design_prompt() -> str:
    _, srs_digest = store.read_accepted("srs")
    input_text = f"""Draft the SDD for this Specification run (blueprint 0004 §2/§3, design phase),
building on the accepted SRS (digest '{srs_digest}').

Write a JSON OBJECT to the file '{SDD_PROPOSAL_PATH}' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: {SDD_SHAPE}
`schema` must be exactly "{SDD_SCHEMA}" and `srsDigest` must be
set to exactly '{srs_digest}'. Every functional and quality requirement from the
accepted SRS must be allocated to (referenced by) at least one ADR's
`requirementIds`. Every ADR id must be unique, and every ADR/control requirement
reference must point at a requirement id that actually exists in the accepted SRS.

Return `design` without arguments when done; the harness will validate the file and
either persist sdd.accepted.json and stop, or re-request `design` with the reported
violations."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "design", ()),
        prompt_formatter.skills("spec-design"),
    )


def design_retry_prompt(violations) -> str:
    _, srs_digest = store.read_accepted("srs")
    input_text = f"""The SDD proposal at '{SDD_PROPOSAL_PATH}' did not pass SddEvaluator:
{_violation_lines(violations)}

Rewrite the file at the exact same path with this shape: {SDD_SHAPE}
`schema` must be exactly "{SDD_SCHEMA}" and `srsDigest` must be
set to exactly '{srs_digest}'. Return `design` without arguments for another
harness-controlled attempt."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "design", ()),
        prompt_formatter.skills("spec-design"),
    )


# --- review ---------------------------------------------------------------------------------


def review_prompt() -> str:
    input_text = f"""Assess readiness for this Specification run (blueprint 0004 §2/§7, blueprint 0006
review phase): judge whether the accepted idea/PRD/SRS/SDD chain is internally
consistent, or whether an earlier phase needs to be redone.

Write a JSON OBJECT to the file '{REVIEW_PROPOSAL_PATH}' (a real file, written with your
file-write tool — NOT escaped or embedded inside the envelope you send back) with this
shape: {REVIEW_SHAPE}
`verdict` must be exactly one of "READY", "FAIL:product", "FAIL:analysis" or
"FAIL:design". `conflicts` and `residuals` are always required arrays (use `[]` when
there are none — never omit them).

If `verdict` is "READY": propose at most 10 readiness slices, each with a unique id.
Every `requirementIds` entry must reference a real requirement from the accepted SRS
and every `adrIds` entry must reference a real ADR from the accepted SDD. Every
`dependsOn` entry must name another slice in this same list (never itself, never an
id outside this proposal); the dependency graph must be acyclic, and at least one
slice must have an empty `dependsOn` (a starting slice). Every requirement in the
accepted SRS must be covered by at least one slice's `requirementIds` — the slices
must form a complete cover.

If `verdict` starts with "FAIL:": leave `slices` empty — a FAIL verdict is a
rejection of an earlier phase, not a slice proposal; explain the rejection through
`conflicts`/`residuals` instead.

Return `review` without arguments when done; the harness will validate the file and
either pause for approval (READY), recascade to the failing phase (FAIL:*), or
re-request `review` with the reported violations."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "review", ()),
        prompt_formatter.skills("spec-review"),
    )


def review_retry_prompt(violations) -> str:
    input_text = f"""The readiness verdict proposal at '{REVIEW_PROPOSAL_PATH}' did not pass
ReadinessEvaluator:
{_violation_lines(violations)}

Rewrite the file at the exact same path with this shape: {REVIEW_SHAPE}
`verdict` must be exactly one of "READY", "FAIL:product", "FAIL:analysis" or
"FAIL:design", and `conflicts`/`residuals` must always be present arrays. Return
`review` without arguments for another harness-controlled attempt."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "review", ()),
        prompt_formatter.skills("spec-review"),
    )


# --- approve --------------------------------------------------------------------------------


def approve_prompt() -> str:
    bundle_digest = store.bundle_digest()
    input_text = f"""Review the bundle for this Specification run before publication (blueprint 0004 §5
ApprovalEvaluator, §6 Publicação segura).

{_bundle_preview()}

The current bundle digest is '{bundle_digest}'.

Write a JSON OBJECT to the file '{APPROVAL_PROPOSAL_PATH}' (a real file, written with
your file-write tool — NOT escaped or embedded inside the envelope you send back)
with this shape: {APPROVAL_SHAPE}
`decision` must be exactly "approved" or "revise". `bundleDigest` must be set to
exactly '{bundle_digest}' — it is re-checked against the CURRENT accepted chain at
evaluation time, so if any accepted document changes after this preview, resend with
the freshly reported digest instead of the one shown here. `rationale` must state a
real reason, not a placeholder.

If `decision` is "approved": the four accepted documents (PRD, SRS, SDD, readiness)
are rendered and published to 'specs/active/' as 00-prd.md,
10-software-requirements-specification.md, 20-software-design-document.md and
30-readiness-handoff.md, and the run completes.

If `decision` is "revise": the run routes back to the review phase so a fresh
readiness verdict — including a FAIL:* one, if a deeper phase needs rework — can be
issued.

Return `approve` without arguments when done; the harness will validate the file and
act on the decision, or re-request `approve` with the reported violations."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "approve", ()),
        prompt_formatter.skills("spec-review"),
    )


def approve_retry_prompt(violations) -> str:
    bundle_digest = store.bundle_digest()
    input_text = f"""The approval proposal at '{APPROVAL_PROPOSAL_PATH}' did not pass ApprovalEvaluator:
{_violation_lines(violations)}

The current bundle digest is '{bundle_digest}'. Rewrite the file at the exact same
path with this shape: {APPROVAL_SHAPE}
`decision` must be exactly "approved" or "revise", `bundleDigest` must be set to
exactly '{bundle_digest}', and `rationale` must state a real reason. Return `approve`
without arguments for another harness-controlled attempt."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "approve", ()),
        prompt_formatter.skills("spec-review"),
    )
