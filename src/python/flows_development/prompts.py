"""Builds the development flow's prompts — the "strategy" kept separate from the state
machine in `tasks.py`. Each step references its output token via a constant (`$XXX`): the
same name the driver fills in and returns as the next envelope's arg.
"""

from __future__ import annotations

from flows_development import state_keys
from harness_engine import artifact_store, prompt_formatter, run_config_store, state_store
from harness_engine.envelope import Envelope, EnvelopeType
from harness_engine.feature_store import DESCRIPTION_MAX_CHARS, Feature

# Output tokens (the driver stores the step's artifact in these and returns them as args).
FEATURES = "$FEATURES"
VERIFY_CMD = "$VERIFY_CMD"
TARGET_DIR = "$TARGET_DIR"
NOTE = "$NOTE"
SMOKE = "$SMOKE"
SUMMARY = "$SUMMARY"
RESULT = "$RESULT"
COMMIT = "$COMMIT"

# Shape of the feature_list embedded in the prompts.
FEATURES_SHAPE = (
    '[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[]}, ...]'
)


def _state(key: str) -> str:
    return state_store.get(key) or ""


def _brief_block() -> str:
    """Reinjects the persisted brief (artifact_store, state_keys.BRIEF_ARTIFACT_NAME) at the
    two points of the loop that actually reason about "what to build" — bearings and
    implement, and only there: smoke/pick/verify/fix/handoff just run a script or do
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
    input_text = f"""You are the INITIALIZER (session 0). From the brief below:
1. Ensure there is a Git repository in the target directory (run `git init` if needed) and create/reuse a dedicated working branch (never commit straight to main/master).
2. Scaffold the target project's environment: create an idempotent `init.sh` that installs dependencies and brings up/builds the app, an idempotent `verify-feature.sh <id>` that verifies a feature, and the minimal folder structure.
3. Expand the brief into a PRIORITIZED list of small, verifiable features, each independently implementable and testable. Number the priority (1 = highest). If a feature only makes sense after another one (e.g. it needs a schema another feature creates), record their ids in `dependsOn` — empty array when there is no dependency. The harness honors this order in addition to priority. Also fill in, for each feature: `description`, an objective description of what it does (up to {DESCRIPTION_MAX_CHARS} characters); and `references`, the explicit codes cited in the brief that relate to it (e.g. "RF-003", "JIRA-142", a named section) — empty array if the brief cites no explicit code for that feature (do not invent one).

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
    input_text = f"""You are the INITIALIZER (session 0). Use the #tool:askQuestions and ask the user:
(a) what to build (the app's goal), (b) the target directory, and (c) the verify
command (e.g. `dotnet test`, `npm test`). Then:
1. Ensure there is a Git repository in the target directory (run `git init` if needed) and create/reuse a dedicated working branch (never commit straight to main/master).
2. Scaffold the environment: create an idempotent `init.sh` and an idempotent `verify-feature.sh <id>` in the target directory.
3. Expand the goal into a PRIORITIZED list of small, verifiable features. If one depends on another, record their ids in `dependsOn` (empty array when there is none). Also fill in `description` (up to {DESCRIPTION_MAX_CHARS} characters) and `references` (explicit codes cited by the user for that feature; empty array if there are none).

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


# --- per-feature loop (one fresh-context session) --------------------------


def bearings_prompt() -> str:
    brief = _brief_block()
    input_text = f"""=== NEW SESSION (clean context) ===
You are a coding agent starting a FRESH session. Do not assume anything from the
previous session — all state lives in the persistent artifacts.
{brief}Get your bearings with short output: run `pwd`, read only the tail of `progress.txt` and the
recent `git log --oneline` to understand what has already been done. Do not paste long
logs; if you need to preserve detail, save it in `.harness/logs/`.

Summarize what you found in '""" + NOTE + "' in 2-4 lines."
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "bearings", (NOTE,)),
        prompt_formatter.skills("dev-bearings"),
    )


def smoke_prompt() -> str:
    input_text = f"""Smoke test: run `./init.sh` in the target directory ({run_config_store.load().target_dir}) and confirm
that the baseline comes up/builds without error before touching any feature. Save the
full output to `.harness/logs/smoke.log` and report in '{SMOKE}' just `ok` or the
main error and the log path."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "smoke", (SMOKE,)),
        prompt_formatter.skills("dev-smoke"),
    )


def pick_prompt() -> str:
    input_text = """Baseline confirmed. Send the `pick` command to receive the next feature to
implement (the highest-priority one still pending — the harness chooses)."""
    return prompt_formatter.format(input_text, Envelope(EnvelopeType.COMMAND, "pick", ()))


def implement_prompt(feature: Feature) -> str:
    brief = _brief_block()
    context = _feature_context_block(feature)
    input_text = f"""Implement EXCLUSIVELY this feature, incrementally and minimally — nothing beyond
it:
{brief}Feature #{feature.id} (priority {feature.priority}): {feature.title}
{context}Work in the target directory ({run_config_store.load().target_dir}). If you run commands with
long output, save it to `.harness/logs/` and do not paste logs into the summary. When done,
summarize what you implemented in '{SUMMARY}' in one short sentence."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "implement", (SUMMARY,)),
        prompt_formatter.skills("dev-implement"),
    )


def verify_prompt() -> str:
    input_text = f"""The harness did not find `verify-feature.sh` in the target directory, so do a
manual self-verify of feature #{_state(state_keys.CURRENT_FEATURE_ID)} ({_state(state_keys.CURRENT_FEATURE_TITLE)})
the way a user would: run `{run_config_store.load().verify_cmd}` in the target directory
({run_config_store.load().target_dir}) and confirm the behavior end to end. Save the
full output to `.harness/logs/verify-{_state(state_keys.CURRENT_FEATURE_ID)}.log`.

Respond in '{RESULT}' starting with `PASS` or `FAIL: <reason>`, including only the
main error and the log path."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "verify", (RESULT,)),
        prompt_formatter.skills("dev-verify"),
    )


def verify_retry_prompt() -> str:
    input_text = f"""The self-verify verdict did not start with `PASS` or `FAIL`. Re-run, if
needed, `{run_config_store.load().verify_cmd}` in the target directory ({run_config_store.load().target_dir})
saving the full output to `.harness/logs/verify-{_state(state_keys.CURRENT_FEATURE_ID)}.log`.
Respond in '{RESULT}' starting exactly with `PASS` or `FAIL: <reason>`, without pasting
long logs."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "verify", (RESULT,)),
        prompt_formatter.skills("dev-verify"),
    )


def fix_prompt(verify_failure: str | None = None) -> str:
    failure = ""
    if verify_failure and verify_failure.strip():
        failure = f"""Failure observed: {verify_failure}

"""
    input_text = f"""Verification FAILED on feature #{_state(state_keys.CURRENT_FEATURE_ID)}
({_state(state_keys.CURRENT_FEATURE_TITLE)}). {failure}Fix the implementation (still ONLY this feature).
If you check logs, read only the relevant excerpt. Summarize the fix in '{SUMMARY}' — we'll
verify again next."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "implement", (SUMMARY,)),
        prompt_formatter.skills("dev-implement"),
    )


def handoff_prompt(automatic_failure: str | None = None) -> str:
    failure = ""
    if automatic_failure and automatic_failure.strip():
        failure = f"""Automatic handoff failed: {automatic_failure}

"""
    input_text = f"""{failure}Leave the state CLEAN for the next session:
1. `git commit` with a descriptive message referencing feature #{_state(state_keys.CURRENT_FEATURE_ID)}. If the target directory is not a Git repository, record this explicitly as `NO_GIT: <reason>`.
2. Append a line to `progress.txt`: feature completed, what was done, and how to verify.

Confirm with the commit hash or `NO_GIT: <reason>` in '{COMMIT}'."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "handoff", (COMMIT,)),
        prompt_formatter.skills("dev-handoff"),
    )


def handoff_retry_prompt() -> str:
    input_text = f"""The handoff confirmation came back empty. Update `progress.txt` in the target directory
({run_config_store.load().target_dir}) and respond in '{COMMIT}' with the commit hash or
`NO_GIT: <reason>` when there is no Git repository."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "handoff", (COMMIT,)),
        prompt_formatter.skills("dev-handoff"),
    )
