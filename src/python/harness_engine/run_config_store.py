"""Persists `verify_cmd`/`target_dir` (captured once by `plan`) to
`.harness/run_config.json` — deliberately outside `state.json`. task_registry
unconditionally resets `state.json` on every `start`, before any domain code runs; a
resumed run still needs these two values for `smoke`/`verify` to work, so they have to
survive that reset.
"""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass
from pathlib import Path

from harness_engine.atomic_io import write_text_atomic

_DIR = ".harness"
_FILE_PATH = ".harness/run_config.json"


@dataclass(frozen=True)
class RunConfig:
    """Verify command, target directory, and run identity (RFC §6.4), all captured once by
    `plan`. `run_id` is generated only on a genuinely new run — the same moment `write()`
    is called after `reset()` — and survives every resume because this file isn't touched
    when `start` decides there's pending work (see the module docstring)."""

    verify_cmd: str = ""
    target_dir: str = "."
    run_id: str = ""

    def to_dict(self) -> dict[str, object]:
        return {"verifyCmd": self.verify_cmd, "targetDir": self.target_dir, "runId": self.run_id}

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "RunConfig":
        return RunConfig(
            verify_cmd=str(payload.get("verifyCmd") or ""),
            target_dir=str(payload.get("targetDir") or "."),
            run_id=str(payload.get("runId") or ""),
        )


def write(config: RunConfig) -> None:
    """Writes the run configuration — same lifecycle as feature_list.json (written by
    `plan`, deleted only when `start` decides there's no run to resume)."""
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        write_text_atomic(_FILE_PATH, json.dumps(config.to_dict(), separators=(",", ":")))
    except Exception as ex:
        print(f"[RunConfigStore] failed to write: {ex}", file=sys.stderr)


def load() -> RunConfig:
    """Reads the persisted configuration, or the defaults if nothing has been written yet."""
    try:
        p = Path(_FILE_PATH)
        if p.exists():
            return RunConfig.from_dict(json.loads(p.read_text()))
    except Exception as ex:
        print(f"[RunConfigStore] failed to load: {ex}", file=sys.stderr)

    return RunConfig()


def reset() -> None:
    """Deletes on a genuinely new run — paired with feature_store.reset()."""
    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        print(f"[RunConfigStore] failed to clear: {ex}", file=sys.stderr)
