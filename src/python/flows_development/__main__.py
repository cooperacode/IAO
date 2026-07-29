"""The "long-running agent" pattern: initializer + loop of fresh sessions, one feature at
a time. No orchestration here — dispatch, guards, and transport live in harness_engine.

    start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
"""

from __future__ import annotations

import sys

from flows_development import tasks
from harness_engine import envelope_validation, feature_store, harness_host

TASKS = {
    "start": lambda _envelope: tasks.start(),
    "plan": tasks.plan,
    "bearings": tasks.bearings,
    "smoke": tasks.smoke,
    "pick": tasks.pick,
    "implement": tasks.implement,
    "verify": tasks.verify,
    "handoff": tasks.handoff,
}

# Contextual expectation per command; a rejection becomes a corrective error (the driver
# fixes and resends). `pick` has no validator — it doesn't carry a driver artifact (the
# selection is the harness's).
VALIDATORS = {
    "plan": envelope_validation.not_empty("the JSON array of features [{id,title,priority}]"),
}


def main(argv: list[str]) -> int:
    # Own snapshots: if this flow shares `.harness/` with other flows (same workspace), it
    # must NOT overwrite the last-run.* that another flow consumes. Freezes at its own path.
    # max_steps: override of the global ceiling (12) — this flow is long-running and needs
    # slack for the loop.
    # should_reset_on_start: a "start" also arrives on the per-feature hard reset (a fresh
    # session reopening a run in progress) — it's only a genuinely new run when there's no
    # pending feature.
    return harness_host.run(
        argv,
        TASKS,
        trace_snapshot_path=".harness/last-development.trace.jsonl",
        state_snapshot_path=".harness/last-development.state.json",
        validators=VALIDATORS,
        max_steps=tasks.STEP_BUDGET,
        should_reset_on_start=lambda: feature_store.pending_count() == 0,
    )


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
