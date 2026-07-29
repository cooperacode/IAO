"""Reusable entry point for a flow. A new domain only needs to define its tasks and call
`run` — all the orchestration (dispatch, guards, transport) lives here.
"""

from __future__ import annotations

from typing import Callable, Mapping

from harness_engine import state_store, task_registry, trace
from harness_engine.task_registry import Action, Validator


def run(
    args: list[str],
    tasks: Mapping[str, Action],
    trace_snapshot_path: str = trace.LAST_RUN_PATH,
    state_snapshot_path: str = state_store.LAST_RUN_STATE_PATH,
    validators: Mapping[str, Validator] | None = None,
    max_steps: int | None = None,
    should_reset_on_start: Callable[[], bool] | None = None,
) -> int:
    result = task_registry.dispatch(args, tasks, validators, max_steps, should_reset_on_start)

    # Run finished: freezes the trajectory AND final state as evidence for later
    # evaluation, before a next flow resets the live trace and state. Each flow publishes
    # to ITS OWN path, so evaluation doesn't overwrite what it itself consumes.
    if result == "stop":
        trace.snapshot(trace_snapshot_path)
        state_store.snapshot(state_snapshot_path)

    # The single place that writes to stdout — the harness transport channel.
    print(result)
    return 0
