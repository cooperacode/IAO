"""Builds the development flow's prompts — the "strategy" kept separate from the state
machine in `tasks.py`. Each step references its output token via a constant (`$XXX`): the
same name the driver fills in and returns as the next envelope's arg.
"""

from __future__ import annotations

from flows_development import state_keys
from harness_engine import artifact_store, context_policy, prompt_formatter, run_config_store, state_store
from harness_engine.envelope import Envelope, EnvelopeType
from harness_engine.feature_store import Feature

# Output tokens (the driver stores the step's artifact in these and returns them as args).
FEATURES = "$FEATURES"
VERIFY_CMD = "$VERIFY_CMD"
TARGET_DIR = "$TARGET_DIR"

# Shape of the feature_list embedded in the prompts.
FEATURES_SHAPE = (
    '[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[]}, ...]'
)


def _state(key: str) -> str:
    return state_store.get(key) or ""


def _brief_block() -> str:
    """Reinjects the persisted brief (artifact_store, state_keys.BRIEF_ARTIFACT_NAME) at the
    point of the loop that actually reasons about "what to build" — implement. The
    smoke/pick/verify/fix/handoff steps only repair setup or do
    bookkeeping, with no need for scope context. Returns a blank line (the same paragraph
    break as before this feature) when there's no persisted brief — interactive mode, or
    resuming a run from before this feature —, or the "<brief>" block when there is one.
    Same treatment as the skills (prompt_formatter._read_skills): line breaks become the
    literal "\\n" marker and the whole block ends up on a single line — the brief content
    doesn't need to preserve its original Markdown formatting here, just be available.
    Always reinjecting the SAME text, byte for byte, is also the lowest-cost bet to
    benefit from the driver's underlying provider's prompt cache (not guaranteed: the
    harness only controls the emitted text, not whether the driver marks a cache
    breakpoint there)."""
    brief = artifact_store.read(state_keys.BRIEF_ARTIFACT_NAME)
    if not brief.strip():
        return "\n"
    single_line = brief.replace("\r\n", "\\n").replace("\n", "\\n")
    return f"<brief>{single_line}</brief>\n"


def _bearings_block() -> str:
    bearings = _state(state_keys.CURRENT_BEARINGS)
    return f"<bearings>{bearings}</bearings>\n" if bearings.strip() else ""


def _feature_context_block(feature: Feature) -> str:
    """Reinjects description/references (harness_engine.feature_store.Feature) into the
    implement prompt — the only point of the loop that receives the whole Feature object,
    not just title/id via state_store. Returns a blank line (the same paragraph break as
    before this feature) when the feature has neither — e.g. a feature_list.json from a
    version before these fields existed — the block disappears, it doesn't show up empty."""
    if not feature.description.strip() and not feature.refs:
        return "\n"
    references = ", ".join(feature.refs) if feature.refs else "none"
    return f"Description: {feature.description}\nBrief references: {references}\n\n"


# --- session 0: initializer -------------------------------------------------


def initializer_prompt(content: str, files: list[str]) -> str:
    input_text = f"""Initialize the development run from this brief by following the injected
`dev-initializer` skill:

<brief sources="{', '.join(files)}">
{content}
</brief>

Store a JSON ARRAY in '{FEATURES}': {FEATURES_SHAPE}
(just the array, no passes — every feature is born pending). Store the verify
command in '{VERIFY_CMD}' (e.g. `dotnet test`, `npm test`) and the target directory
in '{TARGET_DIR}'. The `verify-feature.sh` may run the full suite at the start:
`./init.sh`, then `$VERIFY_CMD`, print `PASS: feature <id> ...` and exit 0."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "plan", (FEATURES, VERIFY_CMD, TARGET_DIR)),
        prompt_formatter.skills("dev-initializer"),
    )


def initializer_interactive() -> str:
    input_text = f"""No brief was supplied. Ask the user for the goal, target directory, and
established verification command, then follow the injected `dev-initializer` skill.

Store a JSON ARRAY in '{FEATURES}' {FEATURES_SHAPE},
the command in '{VERIFY_CMD}' and the directory in '{TARGET_DIR}'. The `verify-feature.sh`
may run the full suite at the start: `./init.sh`, then `$VERIFY_CMD`, print
`PASS: feature <id> ...` and exit 0."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "plan", (FEATURES, VERIFY_CMD, TARGET_DIR)),
        prompt_formatter.skills("dev-initializer"),
    )


def plan_retry_prompt() -> str:
    input_text = f"""Could not parse the feature list. Resend in '{FEATURES}' a valid JSON
ARRAY, in exactly the format {FEATURES_SHAPE} — just the array, no surrounding text.
Repeat the command `{VERIFY_CMD}` and `{TARGET_DIR}`."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "plan", (FEATURES, VERIFY_CMD, TARGET_DIR)),
    )


def smoke_fix_prompt(failure: str) -> str:
    input_text = f"""The deterministic smoke test failed: {failure}
Repair the target setup using `dev-smoke`, then return `smoke` without arguments. The harness
will rerun `init.sh` and decide from its exit code."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "smoke", ()),
        prompt_formatter.skills("dev-smoke"),
    )


def implement_prompt(feature: Feature) -> str:
    brief = _brief_block()
    bearings = _bearings_block()
    context = _feature_context_block(feature)
    input_text = f"""{context_policy.new_feature_prefix()}

Follow `dev-implement` for this feature:
{brief}{bearings}Feature #{feature.id} (priority {feature.priority}): {feature.title}
{context}Target directory: {run_config_store.load().target_dir}

Return `implement` without arguments when done. The harness derives the summary from Git."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "implement", ()),
        prompt_formatter.skills("dev-implement"),
    )


def verify_prompt() -> str:
    input_text = f"""The deterministic verifier could not be started for feature #{_state(state_keys.CURRENT_FEATURE_ID)}
({_state(state_keys.CURRENT_FEATURE_TITLE)}). Repair the verification setup in the target directory
({run_config_store.load().target_dir}).

Repair it using `dev-verify`, then return `verify` without arguments. The harness reruns the
verifier and decides from its process result."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "verify", ()),
        prompt_formatter.skills("dev-verify"),
    )


def verify_retry_prompt() -> str:
    input_text = f"""The deterministic verifier is unavailable for feature #{_state(state_keys.CURRENT_FEATURE_ID)}
({_state(state_keys.CURRENT_FEATURE_TITLE)}). Repair or create it in the target directory
({run_config_store.load().target_dir}).
Repair it using `dev-verify`, then return `verify` without arguments for another
harness-controlled attempt."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "verify", ()),
        prompt_formatter.skills("dev-verify"),
    )


def fix_prompt(verify_failure: str | None = None) -> str:
    failure = ""
    if verify_failure and verify_failure.strip():
        failure = f"""Failure observed: {verify_failure}

"""
    input_text = f"""Verification FAILED on feature #{_state(state_keys.CURRENT_FEATURE_ID)}
({_state(state_keys.CURRENT_FEATURE_TITLE)}). {failure}Follow `dev-implement` to fix only this
feature. Return `implement` without arguments; the harness derives the new summary from Git."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "implement", ()),
        prompt_formatter.skills("dev-implement"),
    )


def handoff_prompt(automatic_failure: str | None = None) -> str:
    failure = ""
    if automatic_failure and automatic_failure.strip():
        failure = f"""Automatic handoff failed: {automatic_failure}

"""
    input_text = f"""{failure}Repair the repository/progress state using `dev-handoff`, then return
`handoff` without arguments. The harness will inspect the repository and retry the real
handoff."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "handoff", ()),
        prompt_formatter.skills("dev-handoff"),
    )
