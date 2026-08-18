from __future__ import annotations
import sys
from harness_engine import harness_host
from . import tasks

TASKS = {
    "start": lambda _: tasks.start(),
    "discover": tasks.discover,
    "product": tasks.product,
    "analysis": tasks.analysis,
    "design": tasks.design,
    "review": tasks.review,
    "approve": tasks.approve,
}


def main(argv):
    return harness_host.run(
        argv,
        TASKS,
        trace_snapshot_path=".harness/last-specification.trace.jsonl",
        state_snapshot_path=".harness/last-specification.state.json",
        max_steps=tasks.STEP_BUDGET,
        should_reset_on_start=lambda: tasks.store.load_run().get("status") != "in_progress",
    )


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
