"""Builds the development flow's prompts — the "strategy" kept separate from the state
machine in `tasks.py`. Each step references its output token via a constant (`$XXX`): the
same name the driver fills in and returns as the next envelope's arg.
"""

from __future__ import annotations

from flows_development import state_keys
from harness_engine import context_policy, feature_store, prompt_formatter, run_config_store, state_store
from harness_engine.envelope import Envelope, EnvelopeType
from harness_engine.feature_store import Feature

# Output tokens (the driver stores the step's artifact in these and returns them as args).
FEATURES = "$FEATURES"
VERIFY_CMD = "$VERIFY_CMD"
TARGET_DIR = "$TARGET_DIR"

# Shape of the feature_list embedded in the prompts.
FEATURES_SHAPE = (
    '[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[],"implementationContext":{"requirements":[],"constraints":[],"files":[],"acceptance":[]}}, ...]'
)


def _state(key: str) -> str:
    return state_store.get(key) or ""


def _feature_context_block(feature: Feature) -> str:
    """Returns the bounded inline context for implement/fix prompts."""
    if not feature.description.strip() and not feature.refs and feature.context.is_empty:
        return "\n"
    references = ", ".join(feature.refs) if feature.refs else "none"
    context_block = f"<implementation-context>{feature.context.prompt_text()}</implementation-context>\n" if not feature.context.is_empty else ""
    return f"Description: {feature.description}\nBrief references: {references}\n{context_block}\n"


def _current_feature_context_block() -> str:
    try:
        feature_id = int(_state(state_keys.CURRENT_FEATURE_ID))
    except ValueError:
        return ""
    feature = next((item for item in feature_store.load() if item.id == feature_id), None)
    return _feature_context_block(feature) if feature is not None else ""


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
    context = _feature_context_block(feature)
    input_text = f"""{context_policy.new_feature_prefix()}

Follow `dev-implement` for this feature:
Feature #{feature.id} (priority {feature.priority}): {feature.title}
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
({_state(state_keys.CURRENT_FEATURE_TITLE)}).
{_current_feature_context_block()}{failure}Follow `dev-implement` to fix only this
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
