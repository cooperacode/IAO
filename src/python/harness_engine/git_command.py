"""Runner pequeno e shell-safe para comandos Git.

A engine fornece o mecanismo; flows decidem quais comandos rodar e como interpretar o
resultado.
"""

from __future__ import annotations

import subprocess
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class GitCommandResult:
    exit_code: int
    output: str
    error: str


def run(working_directory: str | Path, *args: str) -> GitCommandResult:
    try:
        proc = subprocess.run(
            ["git", *args],
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
