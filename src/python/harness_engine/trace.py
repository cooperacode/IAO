"""Writes one line per loop turn to `.harness/trace.jsonl`. It's the foundation of both
telemetry and the trajectory evaluator: state_store only keeps the final state —
overwriting `data` on every step — so without this recorded sequence there's no way to
evaluate the path the agent took.

Cost: zero tokens and one append write per invocation.
"""

from __future__ import annotations

import hashlib
import json
import shutil
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

_DIR = ".harness"
_FILE_PATH = ".harness/trace.jsonl"

# Genesis hash: used as `prev_hash` for a trace's first entry (file absent or empty) —
# 64 zeros, the same length as a sha256 hex digest, so every recorded `prevHash` has a
# uniform shape regardless of its position in the chain.
_GENESIS_HASH = "0" * 64

# Frozen trajectory of the last run that ended in `stop`. harness_host writes here when
# the producer flow finishes, so another flow (evaluation) can read the evidence even
# after resetting the live `trace.jsonl` in its own `start`.
LAST_RUN_PATH = ".harness/last-run.trace.jsonl"

# Frozen trajectory of the last evaluation run. Its own path so evaluation (which also
# ends in `stop`) doesn't overwrite the run's evidence in LAST_RUN_PATH.
LAST_EVALUATION_PATH = ".harness/last-evaluation.trace.jsonl"


class TraceOutcome:
    """Possible outcomes for a step, recorded in `TraceEntry.outcome`."""

    INSTRUCTION = "instruction"  # advanced to the next step
    STOP = "stop"                # normal end of the flow
    ERROR = "error"              # typed error returned to the driver
    BUDGET = "budget"            # cut off by the step ceiling
    TIMEOUT = "timeout"          # cut off by the per-step time ceiling


@dataclass(frozen=True)
class TraceEntry:
    """One loop turn: step, received command, outcome, cost (emitted-instruction chars),
    recording timestamp, and the previous line's hash in the chain (`prev_hash`) —
    chaining that makes a retroactive edit/removal of an entry detectable (the following
    entry stops matching the recorded hash). `prev_hash` defaults to "" so that old
    traces, recorded before this field existed, keep deserializing.

    `label` is the optional, domain-agnostic tag (e.g. "feature:3") that solves the same
    pain as state_store: `step` is a global counter for the entire run, it doesn't
    identify WHICH unit of work the step belongs to. trace only carries the value — the
    flow decides what it means (see flows_development.tasks.pick). Defaults to "" for the
    same reason as prev_hash: parity with traces recorded before this field existed."""

    step: int
    command: str
    outcome: str
    instruction_chars: int
    timestamp: str  # ISO 8601 with offset, recorded as a string (parity with the wire JSON)
    prev_hash: str = ""
    label: str = ""

    def to_dict(self) -> dict[str, object]:
        return {
            "step": self.step,
            "command": self.command,
            "outcome": self.outcome,
            "instructionChars": self.instruction_chars,
            "timestamp": self.timestamp,
            "prevHash": self.prev_hash,
            "label": self.label,
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "TraceEntry":
        return TraceEntry(
            step=int(payload["step"]),
            command=str(payload["command"]),
            outcome=str(payload["outcome"]),
            instruction_chars=int(payload["instructionChars"]),
            timestamp=str(payload["timestamp"]),
            prev_hash=str(payload.get("prevHash") or ""),
            label=str(payload.get("label") or ""),
        )


def reset() -> None:
    """Truncates the trace at the start of a new workflow (alongside state_store.reset)."""
    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        print(f"[Trace] failed to clear: {ex}", file=sys.stderr)


def append(step: int, command: str, outcome: str, instruction_chars: int, label: str = "") -> None:
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        prev_hash = _last_entry_hash()
        entry = TraceEntry(step, command, outcome, instruction_chars, _now_iso(), prev_hash, label)
        line = json.dumps(entry.to_dict(), separators=(",", ":")) + "\n"
        with open(_FILE_PATH, "a") as f:
            f.write(line)  # a single write() — the whole event is atomic at the line level
    except Exception as ex:
        print(f"[Trace] failed to write: {ex}", file=sys.stderr)


def _last_entry_hash() -> str:
    """sha256 hex of the last non-empty line recorded, or the genesis hash if the trace
    is absent/empty (including right after `reset()`) — the chain's root."""
    try:
        p = Path(_FILE_PATH)
        if not p.exists():
            return _GENESIS_HASH

        last_line = ""
        for line in p.read_text().split("\n"):
            if line.strip():
                last_line = line

        if not last_line:
            return _GENESIS_HASH

        return hashlib.sha256(last_line.encode("utf-8")).hexdigest()
    except Exception as ex:
        print(f"[Trace] failed to compute prevHash: {ex}", file=sys.stderr)
        return _GENESIS_HASH


def snapshot(destination: str) -> None:
    """Freezes the live trace at the destination path — the evidence of the finished run."""
    try:
        if Path(_FILE_PATH).exists():
            Path(_DIR).mkdir(parents=True, exist_ok=True)
            shutil.copyfile(_FILE_PATH, destination)
    except Exception as ex:
        print(f"[Trace] failed to freeze: {ex}", file=sys.stderr)


def load() -> list[TraceEntry]:
    """Re-reads the live trace in the order it was recorded."""
    return load_from(_FILE_PATH)


def load_from(path: str) -> list[TraceEntry]:
    """Re-reads a trace from an arbitrary path — input for the evaluators (e.g. the snapshot)."""
    try:
        p = Path(path)
        if not p.exists():
            return []

        entries: list[TraceEntry] = []
        for line in p.read_text().splitlines():
            if not line.strip():
                continue
            entries.append(TraceEntry.from_dict(json.loads(line)))
        return entries
    except Exception as ex:
        print(f"[Trace] failed to load: {ex}", file=sys.stderr)
        return []


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()
