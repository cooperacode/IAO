"""Atomic writes for text files: writes to a temp file in the SAME directory as the
destination and swaps it in via `os.replace` (atomic on the same partition, on both
POSIX and Windows). Prevents a failure mid-write (killed process, full disk) from
leaving the authoritative state file truncated/corrupted — the reader always sees
either the complete previous version or the complete new one, never a mix.
"""

from __future__ import annotations

import os
import uuid
from pathlib import Path


def write_text_atomic(path: str, content: str) -> None:
    """Writes `content` to `path` atomically. The parent directory must exist."""
    destination = Path(path)
    tmp_path = destination.with_name(f"{destination.name}.tmp-{uuid.uuid4().hex}")

    try:
        tmp_path.write_text(content)
        os.replace(tmp_path, destination)
    except BaseException:
        tmp_path.unlink(missing_ok=True)
        raise
