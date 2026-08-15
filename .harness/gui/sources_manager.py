"""CRUD over `specs/sources/` — the flat, non-recursive folder the Specification engine reads
exactly once at `start` (see `SpecificationTasks.SourcesFolder` / `DocsReader` in
`src/dotnet/Flows.Specification` and `src/dotnet/Harness.Engine`). Only `.md`/`.txt` files are
ever ingested by the engine, so this module enforces the same allowlist — anything else would
silently be ignored downstream, which is worse than rejecting it up front in the GUI.

`specs/` is entirely gitignored (input and `specs/active/` publish output alike); this is a
local scratch area, not version-controlled state.
"""
from __future__ import annotations

import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
SOURCES_DIR = REPO_ROOT / "specs" / "sources"
HARNESS_CONFIG_PATH = REPO_ROOT / "harness.json"
ALLOWED_EXTENSIONS = {".md", ".txt"}
DEFAULT_DOCS_MAX_CHARS = 40000  # engine default when harness.json omits the field


def docs_max_chars() -> int:
    try:
        config = json.loads(HARNESS_CONFIG_PATH.read_text(encoding="utf-8"))
        value = config.get("docsMaxChars")
        if isinstance(value, int) and value > 0:
            return value
    except (OSError, json.JSONDecodeError):
        pass
    return DEFAULT_DOCS_MAX_CHARS


def _validate_filename(name: str) -> str:
    if not name or not name.strip():
        raise ValueError("filename must not be empty")
    if "/" in name or "\\" in name or ".." in name:
        raise ValueError("filename must not contain path separators or '..'")
    suffix = Path(name).suffix.lower()
    if suffix not in ALLOWED_EXTENSIONS:
        raise ValueError(f"only {', '.join(sorted(ALLOWED_EXTENSIONS))} files are accepted (got '{suffix or name}')")
    return name


def _resolve(name: str) -> Path:
    _validate_filename(name)
    target = (SOURCES_DIR / name).resolve()
    sources_root = SOURCES_DIR.resolve()
    if target.parent != sources_root:
        raise ValueError("filename must resolve to a direct child of specs/sources")
    return target


def list_files() -> dict[str, Any]:
    files: list[dict[str, Any]] = []
    if SOURCES_DIR.is_dir():
        for entry in sorted(SOURCES_DIR.iterdir(), key=lambda p: p.name.lower()):
            if not entry.is_file() or entry.suffix.lower() not in ALLOWED_EXTENSIONS:
                continue
            stat = entry.stat()
            files.append({
                "name": entry.name,
                "sizeBytes": stat.st_size,
                "modifiedAt": datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc)
                    .strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z",
            })
    total_bytes = sum(item["sizeBytes"] for item in files)
    return {
        "files": files,
        "totalBytes": total_bytes,
        "docsMaxChars": docs_max_chars(),
        "folder": "specs/sources",
    }


def save_file(name: str, content: str) -> dict[str, Any]:
    target = _resolve(name)
    SOURCES_DIR.mkdir(parents=True, exist_ok=True)
    target.write_text(content, encoding="utf-8")
    stat = target.stat()
    return {
        "name": target.name,
        "sizeBytes": stat.st_size,
        "modifiedAt": datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc)
            .strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z",
    }


def delete_file(name: str) -> None:
    target = _resolve(name)
    if not target.is_file():
        raise FileNotFoundError(name)
    os.remove(target)
