"""Every harness invocation is a fresh, memoryless process. This store persists the
accumulated state (step counter + domain data) to a file, so the envelope carried by the
model stays minimal — a token saving: the model passes a key, not the entire state, on
every loop turn.
"""

from __future__ import annotations

import json
import shutil
import sys
from dataclasses import replace
from pathlib import Path

from harness_engine.atomic_io import write_text_atomic
from harness_engine.harness_state import HarnessState

_DIR = ".harness"
_FILE_PATH = ".harness/state.json"

# Frozen final state of the last completed run. Exists for the same reason as
# trace.LAST_RUN_PATH: any flow's `start` resets the live `state.json`, so evaluation
# (which checks completeness) needs to read the domain keys from a stable snapshot, not
# from the file its own `start` just zeroed out.
LAST_RUN_STATE_PATH = ".harness/last-run.state.json"

# Frozen final state of the last evaluation run — its own path, so it doesn't overwrite
# refinement's.
LAST_EVALUATION_STATE_PATH = ".harness/last-evaluation.state.json"

# Conventional key in HarnessState.data for the label that task_registry propagates to
# the trace on every step (see trace.TraceEntry.label). Generic on purpose: the engine
# doesn't know what a "feature" is — it only re-reads this key if the flow has set it
# (e.g. flows_development.tasks.pick).
TRACE_LABEL_KEY = "trace_label"


def load() -> HarnessState:
    return load_from(_FILE_PATH)


def load_from(path: str) -> HarnessState:
    """Loads a state from an arbitrary path (e.g. the evidence for a golden set case)."""
    try:
        p = Path(path)
        if p.exists():
            payload = json.loads(p.read_text())
            return HarnessState.from_dict(payload)
    except Exception as ex:
        print(f"[StateStore] failed to load: {ex}", file=sys.stderr)

    return HarnessState(step=0, data={})


def save(state: HarnessState) -> None:
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        write_text_atomic(_FILE_PATH, json.dumps(state.to_dict(), separators=(",", ":")))
    except Exception as ex:
        print(f"[StateStore] failed to save: {ex}", file=sys.stderr)


def reset() -> None:
    save(HarnessState(step=0, data={}))


def snapshot(destination: str) -> None:
    """Freezes the live `state.json` at the destination — the evidence of completeness for the finished run."""
    try:
        if Path(_FILE_PATH).exists():
            Path(_DIR).mkdir(parents=True, exist_ok=True)
            shutil.copyfile(_FILE_PATH, destination)
    except Exception as ex:
        print(f"[StateStore] failed to freeze: {ex}", file=sys.stderr)


def increment() -> int:
    state = load()
    next_step = state.step + 1
    save(replace(state, step=next_step))
    return next_step


def add_cost(chars: int) -> int:
    """Adds the turn's cost to the run's accumulator and returns the total — input to
    the cost ceiling in task_registry. Emitted-instruction chars are the only measure:
    it's what the engine can attest on its own, without depending on the driver's self-report."""
    state = load()
    next_state = replace(state, cost_chars=state.cost_chars + chars)
    save(next_state)
    return next_state.cost_chars


def set(key: str, value: str) -> None:
    state = load()
    state.data[key] = value
    save(state)


def get(key: str) -> str | None:
    state = load()
    return state.data.get(key)


def set_context(context: dict[str, str]) -> None:
    """Persists the driver context captured on `start` (see task_registry)."""
    state = load()
    save(replace(state, context=context))


def get_context() -> dict[str, str] | None:
    """Persisted driver context, for prompt_formatter to reinject into every output."""
    return load().context


def mark_terminal(reason: str) -> None:
    """Latches a hard-stop reason across the next fresh-process invocation."""
    state = load()
    save(replace(state, terminal_reason=reason))


def terminal_reason() -> str | None:
    """Returns the latched hard-stop reason, if this run is already terminal."""
    return load().terminal_reason
