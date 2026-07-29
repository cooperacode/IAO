"""Resolves paths relative to the working directory (repo root, from where the driver
invokes the harness), with a fallback to the package directory. Shared by whoever
injects files into the prompt (skills, docs).
"""

from __future__ import annotations

import os
from pathlib import Path


def resolve(path: str) -> str:
    trimmed = path.strip()
    if Path(trimmed).is_absolute():
        return trimmed

    cwd = Path.cwd().resolve()
    from_cwd = (cwd / trimmed).resolve()
    if os.path.exists(from_cwd) and _is_contained(from_cwd, cwd):
        return str(from_cwd)

    base_dir = Path(__file__).resolve().parent
    from_base = (base_dir / trimmed).resolve()
    if _is_contained(from_base, base_dir):
        return str(from_base)

    # Neither the CWD nor the package fallback contains the result (a symlink escaped
    # outside the base) — return the original join, without following the link; the
    # caller's existence check naturally fails on this unresolved path. Full containment
    # against a signed policy root is future-phase work (capability broker, RFC §6.9) —
    # this is just the minimal symlink-escape rejection from RFC §6.3.
    return str(base_dir / trimmed)


def _is_contained(resolved: Path, base: Path) -> bool:
    """`resolved` is inside `base` by REAL path (not string prefix) — follows symlinks
    (`.resolve()` already did that) and compares the final tree."""
    try:
        return resolved == base or resolved.is_relative_to(base)
    except ValueError:
        return False
