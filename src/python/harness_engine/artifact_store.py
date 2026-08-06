"""Persists each flow artifact in its own file (`.harness/<name>.md`) and keeps a
manifest (`.harness/artifacts.json`) with the write order. The manifest is the contract
between producer and consumer: evaluation reads the artifacts through it, without
depending on a combined report.

Only the PRODUCER flow resets the manifest (on its `start`) — the consumer (evaluation)
doesn't touch it, for the same reason as the trace/state_store snapshots: the
evaluator's start must not erase the evidence it is itself about to read.
"""

from __future__ import annotations

import json
from pathlib import Path

from harness_engine import harness_log
from harness_engine.atomic_io import write_text_atomic

_DIR = ".harness"
MANIFEST_PATH = ".harness/artifacts.json"


def reset() -> None:
    """Deletes the previous run's artifacts and the manifest — called by the producer flow on start."""
    try:
        for file in files():
            Path(file).unlink(missing_ok=True)
        Path(MANIFEST_PATH).unlink(missing_ok=True)
    except Exception as ex:
        harness_log.error(f"[ArtifactStore] failed to clear: {ex}")


def write(name: str, content: str) -> str:
    """Writes `.harness/<name>.md` and registers the path in the manifest (once, in arrival order)."""
    path = str(Path(_DIR) / f"{name}.md")

    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        write_text_atomic(path, content)

        current_files = list(files())
        if path not in current_files:
            current_files.append(path)
            _save_manifest(current_files)
    except Exception as ex:
        harness_log.error(f"[ArtifactStore] failed to write {name}: {ex}")

    return path


def read(name: str) -> str:
    """Reads a single artifact by name (e.g. for reinjection into prompts). "" if absent/unreadable."""
    path = Path(_DIR) / f"{name}.md"

    try:
        if path.exists():
            return path.read_text()
    except Exception as ex:
        harness_log.error(f"[ArtifactStore] failed to read {name}: {ex}")

    return ""


def files() -> list[str]:
    """Paths registered in the manifest, in the order they were written."""
    try:
        p = Path(MANIFEST_PATH)
        if p.exists():
            payload = json.loads(p.read_text())
            result = payload.get("files") if isinstance(payload, dict) else None
            if isinstance(result, list):
                return [str(f) for f in result]
    except Exception as ex:
        harness_log.error(f"[ArtifactStore] failed to load manifest: {ex}")

    return []


def has_artifacts() -> bool:
    """Are there artifacts written and present on disk?"""
    return any(Path(f).exists() for f in files())


def read_all() -> str:
    """Concatenates the artifacts in manifest order — the LLM judge's input."""
    parts: list[str] = []

    for file in files():
        try:
            p = Path(file)
            if p.exists():
                parts.append(p.read_text().rstrip() + "\n")
        except Exception as ex:
            harness_log.error(f"[ArtifactStore] failed to read {file}: {ex}")

    return "".join(parts).rstrip()


def _save_manifest(file_list: list[str]) -> None:
    Path(_DIR).mkdir(parents=True, exist_ok=True)
    write_text_atomic(MANIFEST_PATH, json.dumps({"files": file_list}, separators=(",", ":")))
