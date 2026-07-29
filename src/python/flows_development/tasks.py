"""Long-running development flow (the "Effective harnesses for long-running agents"
pattern, Anthropic). An initializer (session 0) expands the brief into a prioritized
feature list; then a loop of fresh-context sessions implements ONE feature at a time:

    start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*

State that survives hard resets lives in persistent artifacts: feature_store
(feature_list.json, from the harness) and progress.txt + git (from the target
directory). Each task only performs effects and decides the NEXT command (the output
envelope) — orchestration (dispatch, global guards, transport) lives in harness_engine.

Prompts in `prompts.py`.
"""

from __future__ import annotations

import subprocess
import sys
import uuid
from dataclasses import replace
from datetime import datetime, timezone
from pathlib import Path

import harness_engine
from flows_development import prompts, state_keys
from harness_engine import (
    artifact_store,
    docs_reader,
    feature_store,
    git_command,
    harness_config,
    run_config_store,
    state_store,
)
from harness_engine.envelope import Envelope
from harness_engine.run_config_store import RunConfig

# This flow's local guards (the global harness.json ceiling, 12, is too short for a loop).
# Few features + a PER-FEATURE step ceiling: bars an implement<->verify loop that never closes.
MAX_FEATURES = 10
STEPS_PER_FEATURE = 8

# Effective step ceiling passed to harness_host (override of the global one): slack for
# the worst case of MAX_FEATURES features spending STEPS_PER_FEATURE each, plus start/plan
# and the boundaries.
STEP_BUDGET = MAX_FEATURES * STEPS_PER_FEATURE + 8


def _state(key: str) -> str:
    return state_store.get(key) or ""


def _docs_folder() -> str:
    return harness_config.current().docs_folder


def start() -> str:
    # A previous session (perhaps from another driver — tokens ran out in one IDE and
    # another takes over) may have died mid-feature. Restarting would discard work in
    # progress; resuming is safe and deterministic: bearings is reentrant by construction
    # (it only rearms the per-feature guard) and the next pick() reselects the same,
    # still-pending feature — without needing to know exactly where the previous session
    # stopped.
    if feature_store.pending_count() > 0:
        print(
            "[dev] run in progress detected (pending feature); resuming via bearings instead of resetting.",
            file=sys.stderr,
        )
        return prompts.bearings_prompt()

    # PRODUCER flow of the feature list: a new run discards the previous one's.
    feature_store.reset()
    run_config_store.reset()
    # Without this, a new run in interactive mode (no docs/) would silently inherit a
    # previous run's brief.md — interactive mode never calls artifact_store.write, so only
    # this reset guarantees no brief from an old topic survives.
    artifact_store.reset()

    # The brief (what to build) comes from docs/, or, without docs, from interactive mode.
    if not docs_reader.has_docs(_docs_folder()):
        return prompts.initializer_interactive()

    content, files = docs_reader.read(_docs_folder())
    # Persisted to be reinjected into bearings/implement (prompts.py) — before this
    # feature, "content" was just a local variable of this turn, discarded as soon as the
    # initializer finished.
    artifact_store.write(state_keys.BRIEF_ARTIFACT_NAME, content)
    state_store.set("origem", "docs")
    return prompts.initializer_prompt(content, files)


def plan(envelope: Envelope | None) -> str:
    features = feature_store.parse(_arg(envelope))
    if not features:
        return prompts.plan_retry_prompt()  # didn't parse → re-request (corrective loop)

    # Feature ceiling: keeps the highest-priority ones (lowest number).
    capped = sorted(features, key=lambda f: (f.priority, f.id))[:MAX_FEATURES]

    # Sanitizes depends_on: a surviving feature may depend on an id cut above, which would
    # block it forever (never "ready") with no way for the driver to know — the harness
    # did the cutting, not it. Trimming nodes from an already-acyclic graph (validated in
    # feature_store.parse) cannot create a cycle, so only cleaning dangling refs is necessary.
    capped_ids = {f.id for f in capped}
    capped = [replace(f, depends_on=tuple(d for d in f.deps if d in capped_ids)) for f in capped]

    feature_store.write(capped)

    # Verification command, target directory, and run identity: rehydrated on every
    # smoke/verify step. Outside state.json on purpose — see run_config_store. run_id is
    # born here (the same moment start() decided this is a new, not resumed, run) and
    # survives every following session without needing to appear in the envelope
    # exchanged with the model (RFC §6.4 — run identity is a control-plane concern, not
    # the contract's).
    run_config_store.write(RunConfig(
        _arg_at(envelope, 1, "dotnet test"),
        _arg_at(envelope, 2, "."),
        str(uuid.uuid4()),
    ))

    return prompts.bearings_prompt()


def bearings(envelope: Envelope | None) -> str:
    # New session (one feature): resets the per-feature guard counter.
    state_store.set(state_keys.FEATURE_STEPS, "1")
    return prompts.smoke_prompt()


def smoke(envelope: Envelope | None) -> str:
    return _stop("per-feature guard") if _over_feature_budget() else prompts.pick_prompt()


def pick(envelope: Envelope | None) -> str:
    if _over_feature_budget():
        return _stop("per-feature guard")

    # DETERMINISTIC selection: highest priority among the ready ones (dependencies satisfied).
    # The harness chooses, not the LLM.
    next_feature = feature_store.next_pending()
    if next_feature is None:
        # pending_count() == 0 is the normal case (handoff would already have closed
        # things out). Pending > 0 is only reachable via a hand-edited feature_list.json
        # outside the graph validated in plan (write/mark_passed don't revalidate) —
        # doesn't fake success in that case.
        return (
            _done()
            if feature_store.pending_count() == 0
            else _stop("blocked dependencies — no pending feature is ready")
        )

    state_store.set(state_keys.CURRENT_FEATURE_ID, str(next_feature.id))
    state_store.set(state_keys.CURRENT_FEATURE_TITLE, next_feature.title)
    # Labels the trace with the current feature (see trace.TraceEntry.label) — without
    # this, every trace.jsonl line only has the global step, with no indication of which
    # feature it belongs to.
    state_store.set(state_store.TRACE_LABEL_KEY, f"feature:{next_feature.id}")
    return prompts.implement_prompt(next_feature)


def implement(envelope: Envelope | None) -> str:
    if _over_feature_budget():
        return _stop("per-feature guard")

    summary = _arg(envelope).strip()
    if summary:
        state_store.set(state_keys.CURRENT_FEATURE_SUMMARY, summary)

    attempted, success, result = _try_automated_verify()
    if attempted:
        state_store.set(state_keys.CURRENT_FEATURE_VERIFY, result)
        return _complete_verified_feature(result) if success else prompts.fix_prompt(result)

    return prompts.verify_prompt()


def verify(envelope: Envelope | None) -> str:
    if _over_feature_budget():
        return _stop("per-feature guard")

    # FAILED → back to implementing the SAME feature (correction loop, bounded by the
    # guard). PASSED → the harness performs the deterministic handoff (progress + git)
    # without spending a model turn; if that fails, falls back to the legacy manual-repair prompt.
    result = _arg(envelope).strip()
    if result.upper().startswith("FAIL"):
        return prompts.fix_prompt(result)

    if result.upper().startswith("PASS"):
        state_store.set(state_keys.CURRENT_FEATURE_VERIFY, result)
        return _complete_verified_feature(result)

    return prompts.verify_retry_prompt()


def handoff(envelope: Envelope | None) -> str:
    if not _arg(envelope).strip():
        return prompts.handoff_retry_prompt()

    try:
        feature_id = int(_state(state_keys.CURRENT_FEATURE_ID))
        feature_store.mark_passed(feature_id)
    except ValueError:
        pass

    # Any feature still pending? Yes → next session (bearings). No → done.
    return _done() if feature_store.all_passing() else prompts.bearings_prompt()


# --- guards and termination -------------------------------------------------


def _complete_verified_feature(verify_result: str) -> str:
    ok, confirmation, failure = _try_automated_handoff(verify_result)
    if not ok:
        print(f"[dev] automatic handoff failed: {failure}", file=sys.stderr)
        return prompts.handoff_prompt(failure)

    print(f"[dev] automatic handoff completed: {confirmation}", file=sys.stderr)
    try:
        feature_store.mark_passed(int(_state(state_keys.CURRENT_FEATURE_ID)))
    except ValueError:
        pass

    return _done() if feature_store.all_passing() else prompts.bearings_prompt()


def _try_automated_handoff(verify_result: str) -> tuple[bool, str, str | None]:
    try:
        feature_id = int(_state(state_keys.CURRENT_FEATURE_ID))
    except ValueError:
        return False, "", "current feature missing from state.json"

    feature = next((f for f in feature_store.load() if f.id == feature_id), None)
    title = feature.title if feature is not None else _state(state_keys.CURRENT_FEATURE_TITLE)
    title = title or f"feature #{feature_id}"
    config = run_config_store.load()
    try:
        target_dir = _resolve_target_dir(config.target_dir)
    except ValueError as ex:
        return False, "", f"invalid target directory: {ex}"

    try:
        target_dir.mkdir(parents=True, exist_ok=True)
        _append_progress(target_dir, feature_id, title, config.verify_cmd, verify_result)
    except Exception as ex:
        return False, "", f"failed to update progress.txt: {ex}"

    rev_parse = git_command.run(target_dir, "rev-parse", "--show-toplevel")
    if rev_parse.exit_code != 0:
        return True, f"NO_GIT: {_one_line(rev_parse.error, 'target directory is outside a Git repository')}", None

    add = git_command.run(target_dir, "add", "-A", "--", ".", ":(exclude).harness")
    if add.exit_code != 0:
        return False, "", f"git add failed: {_one_line(add.error, add.output)}"

    diff = git_command.run(target_dir, "diff", "--cached", "--quiet", "--", ".", ":(exclude).harness")
    if diff.exit_code == 0:
        head = git_command.run(target_dir, "rev-parse", "--short", "HEAD")
        return True, _one_line(head.output, "NO_CHANGES") if head.exit_code == 0 else "NO_CHANGES", None
    if diff.exit_code > 1:
        return False, "", f"git diff --cached failed: {_one_line(diff.error, diff.output)}"

    commit = git_command.run(
        target_dir, "commit", "-m", _commit_message(feature_id, title), "--", ".", ":(exclude).harness")
    if commit.exit_code != 0:
        return False, "", f"git commit failed: {_one_line(commit.error, commit.output)}"

    status = git_command.run(target_dir, "status", "--short", "--", ".", ":(exclude).harness")
    if status.exit_code != 0:
        return False, "", f"git status failed: {_one_line(status.error, status.output)}"
    if status.output.strip():
        return False, "", f"target directory still dirty after commit: {_one_line(status.output)}"

    head = git_command.run(target_dir, "rev-parse", "--short", "HEAD")
    if head.exit_code != 0:
        return False, "", f"commit created, but the hash could not be read: {_one_line(head.error, head.output)}"
    return True, _one_line(head.output, "COMMIT_CREATED"), None


def _try_automated_verify() -> tuple[bool, bool, str]:
    try:
        feature_id = int(_state(state_keys.CURRENT_FEATURE_ID))
    except ValueError:
        return False, False, ""

    try:
        target_dir = _resolve_target_dir(run_config_store.load().target_dir)
    except ValueError as ex:
        print(f"[dev] invalid target directory, automatic verify not attempted: {ex}", file=sys.stderr)
        return False, False, ""

    script = target_dir / "verify-feature.sh"
    if not script.exists():
        return False, False, ""

    try:
        proc = subprocess.run(
            ["bash", str(script), str(feature_id)],
            cwd=target_dir,
            text=True,
            capture_output=True,
            check=False,
            timeout=_verify_timeout_seconds(),
        )
    except subprocess.TimeoutExpired as ex:
        output = _coerce_output(ex.stdout)
        error = _coerce_output(ex.stderr)
        log_path = _write_verify_log(target_dir, script, feature_id, -1, True, output, error)
        return (
            True,
            False,
            f"FAIL: verify-feature.sh {feature_id} exceeded timeout ({_verify_timeout_description()})"
            + _verify_output_suffix(output, error, log_path),
        )
    except Exception as ex:
        error = str(ex)
        log_path = _write_verify_log(target_dir, script, feature_id, -1, False, "", error)
        return (
            True,
            False,
            f"FAIL: verify-feature.sh {feature_id} did not start: {_snippet(error)}{_log_suffix(log_path)}",
        )

    log_path = _write_verify_log(target_dir, script, feature_id, proc.returncode, False, proc.stdout, proc.stderr)
    if proc.returncode == 0:
        return True, True, _pass_result(feature_id, proc.stdout, proc.stderr, log_path)

    return (
        True,
        False,
        f"FAIL: verify-feature.sh {feature_id} failed (exit {proc.returncode})"
        + _verify_output_suffix(proc.stdout, proc.stderr, log_path),
    )


def _resolve_target_dir(target_dir: str) -> Path:
    """Resolves the absolute target directory and rejects the minimal list of clearly
    dangerous destinations from RFC §6.3 (filesystem root, user home, the harness's own
    install root). Full containment against a signed policy root is future-phase work
    (capability broker) — this only blocks what today would, in practice, always be a
    configuration error."""
    if not (target_dir or "").strip():
        raise ValueError("target_dir empty/whitespace is not a valid target directory.")

    resolved = Path(target_dir or ".").resolve()

    if resolved.parent == resolved:
        raise ValueError(f"target_dir resolves to the filesystem root ('{resolved}').")

    if resolved == Path.home().resolve():
        raise ValueError(f"target_dir resolves to the user's home directory ('{resolved}').")

    if resolved == _harness_install_root():
        raise ValueError(f"target_dir resolves to the harness install directory ('{resolved}').")

    return resolved


def _harness_install_root() -> Path:
    """Install/distribution root of the IAO harness (proxy: the root of `src/python`, two
    levels above `harness_engine/__init__.py`)."""
    return Path(harness_engine.__file__).resolve().parent.parent


def _append_progress(
    target_dir: Path,
    feature_id: int,
    title: str,
    verify_cmd: str,
    verify_result: str,
) -> None:
    summary = _one_line(_state(state_keys.CURRENT_FEATURE_SUMMARY), "implementation completed")
    verify = _one_line(verify_result, "PASS")
    command = verify_cmd.strip() or "the project's verify command"
    line = (
        f"[{datetime.now(timezone.utc):%Y-%m-%d %H:%M} UTC] Feature #{feature_id} - {_one_line(title)}: "
        f"{summary}. Verify with: {_one_line(command)}. Result: {verify}"
    )
    with (target_dir / "progress.txt").open("a", encoding="utf-8") as fh:
        fh.write(line + "\n")


def _commit_message(feature_id: int, title: str) -> str:
    suffix = _one_line(title)
    if len(suffix) > 72:
        suffix = suffix[:72].rstrip()
    return f"feat(development): complete feature #{feature_id} - {suffix}"


def _write_verify_log(
    target_dir: Path,
    script: Path,
    feature_id: int,
    exit_code: int,
    timed_out: bool,
    output: str,
    error: str,
) -> str:
    relative_path = Path(".harness/logs") / f"verify-feature-{feature_id}.log"
    try:
        log_path = relative_path.resolve()
        log_path.parent.mkdir(parents=True, exist_ok=True)
        log_path.write_text(
            "\n".join([
                f"timestampUtc: {datetime.now(timezone.utc).isoformat()}",
                f"command: bash ./verify-feature.sh {feature_id}",
                f"cwd: {target_dir}",
                f"script: {script}",
                f"exitCode: {exit_code}",
                f"timedOut: {timed_out}",
                "",
                "--- stdout ---",
                output,
                "",
                "--- stderr ---",
                error,
                "",
            ]),
            encoding="utf-8",
        )
    except Exception as ex:
        return f"log unavailable ({_one_line(str(ex))})"

    return relative_path.as_posix()


def _verify_timeout_seconds() -> float | None:
    timeout_ms = harness_config.current().timeout_ms
    if timeout_ms <= 0:
        return None

    margin = min(500, max(1, timeout_ms // 10))
    return max(0.001, (timeout_ms - margin) / 1000)


def _verify_timeout_description() -> str:
    seconds = _verify_timeout_seconds()
    return "no limit" if seconds is None else f"{int(seconds * 1000)}ms"


def _pass_result(feature_id: int, output: str, error: str, log_path: str) -> str:
    first_line = _first_meaningful_line(output, error)
    result = (
        _snippet(first_line)
        if first_line.upper().startswith("PASS")
        else f"PASS: verify-feature.sh {feature_id} passed"
    )
    return result + _log_suffix(log_path)


def _verify_output_suffix(output: str | None, error: str | None, log_path: str) -> str:
    text = _snippet(_first_meaningful_line(output, error))
    return _log_suffix(log_path) if not text else f": {text}{_log_suffix(log_path)}"


def _first_meaningful_line(*values: str | None) -> str:
    for value in values:
        for line in (value or "").replace("\r", "\n").split("\n"):
            if line.strip():
                return line.strip()
    return ""


def _coerce_output(value: str | bytes | None) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode(errors="replace")
    return value


def _log_suffix(log_path: str) -> str:
    return f". Log: {log_path}" if log_path.strip() else ""


def _snippet(value: str, max_chars: int = 240) -> str:
    text = _one_line(value)
    encoded = text.encode("utf-8")
    if len(encoded) <= max_chars:
        return text
    # Cuts at UTF-8 bytes, not codepoints — decoding with errors="ignore" automatically
    # discards any multibyte sequence cut in half at the limit's edge.
    return encoded[:max_chars].decode("utf-8", errors="ignore").rstrip() + "..."


def _one_line(value: str | None, fallback: str = "") -> str:
    normalized = " ".join((value or "").replace("\r", " ").replace("\n", " ").split())
    return normalized.strip() or fallback


def _over_feature_budget() -> bool:
    """Increments the session counter and signals whether the per-feature ceiling was exceeded."""
    steps = _int_or(_state(state_keys.FEATURE_STEPS), 0) + 1
    state_store.set(state_keys.FEATURE_STEPS, str(steps))

    if steps > STEPS_PER_FEATURE:
        print(
            f"[dev] feature '{_state(state_keys.CURRENT_FEATURE_TITLE)}' exceeded {STEPS_PER_FEATURE} "
            "steps; stopping.",
            file=sys.stderr,
        )
        return True
    return False


def _stop(motivo: str) -> str:
    print(f"[dev] stopped due to {motivo}. feature_list in .harness/feature_list.json", file=sys.stderr)
    return "stop"


def _done() -> str:
    print(
        f"[dev] all {len(feature_store.load())} features pass; done. "
        "State in .harness/feature_list.json",
        file=sys.stderr,
    )
    return "stop"


def _arg(envelope: Envelope | None) -> str:
    return envelope.args[0] if envelope is not None and envelope.args else ""


def _arg_at(envelope: Envelope | None, index: int, fallback: str) -> str:
    if envelope is not None and envelope.args and len(envelope.args) > index and envelope.args[index].strip():
        return envelope.args[index]
    return fallback


def _int_or(value: str, default: int) -> int:
    try:
        return int(value)
    except ValueError:
        return default
