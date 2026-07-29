"""Small, shell-safe runner for Git commands.

The engine provides the mechanism; flows decide which commands to run and how to
interpret the result.
"""

from __future__ import annotations

import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

# Stable, always-empty directory used as `core.hooksPath` for every git command the
# harness fires: neutralizes the target repo's hooks (pre-commit/post-commit etc.), which
# would otherwise run arbitrary code controlled by the very agent being supervised.
_NO_HOOKS_DIR = Path(tempfile.gettempdir()) / "iao-no-hooks"


@dataclass(frozen=True)
class GitCommandResult:
    exit_code: int
    output: str
    error: str


def run(working_directory: str | Path, *args: str) -> GitCommandResult:
    try:
        _NO_HOOKS_DIR.mkdir(parents=True, exist_ok=True)
        proc = subprocess.run(
            [
                "git",
                "-c", f"core.hooksPath={_NO_HOOKS_DIR}",
                "-c", "credential.helper=",
                "-c", "core.pager=cat",
                *args,
            ],
            cwd=working_directory,
            text=True,
            capture_output=True,
            check=False,
        )
    except Exception as ex:
        return GitCommandResult(exit_code=-1, output="", error=str(ex))

    return GitCommandResult(
        exit_code=proc.returncode,
        output=proc.stdout,
        error=proc.stderr,
    )
