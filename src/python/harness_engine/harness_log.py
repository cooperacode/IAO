"""Append-only, human-readable engine log at `.harness/harness.log` — persisted
counterpart to what today only reaches ephemeral stderr (`error`), plus the step
entry/exit markers (`info`, written by task_registry.dispatch) that make an in-flight
step observable before it completes. `trace` only records a COMPLETED turn — during a
slow step, or one that crashes mid-flight, `trace.jsonl` alone gives no evidence the
harness ever picked up the work. This file is that evidence.

Deliberately separate from `trace.jsonl`: the trace is a hash-chained,
one-line-per-turn audit artifact consumed by evaluators and cost-correlation tooling —
doubling it with entry/exit lines would break that "one line = one turn" contract for
every consumer. `harness.log` carries no such contract; it's free-form and append-only.
"""

from __future__ import annotations

import sys
from datetime import datetime, timezone
from pathlib import Path

_DIR = ".harness"
_FILE_PATH = ".harness/harness.log"


def reset() -> None:
    """Truncates the log at the start of a new workflow (alongside trace.reset)."""
    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        print(f"[HarnessLog] failed to clear: {ex}", file=sys.stderr)


def info(message: str) -> None:
    """Liveness/diagnostic events (step entry/exit) — file only, no stderr echo per turn."""
    _write("INFO", message)


def error(message: str) -> None:
    """Every harness-level failure — protocol errors, guard cutoffs, store I/O failures,
    unhandled faults. Writes to stderr too (existing visible behavior every call site
    already relied on) so this is a drop-in replacement for the raw
    `print(..., file=sys.stderr)` calls scattered across the engine."""
    print(message, file=sys.stderr)
    _write("ERROR", message)


def _write(level: str, message: str) -> None:
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        timestamp = datetime.now(timezone.utc).isoformat()
        with open(_FILE_PATH, "a") as f:
            f.write(f"[{timestamp}] [{level}] {message}\n")
    except Exception as ex:
        print(f"[HarnessLog] failed to write: {ex}", file=sys.stderr)
