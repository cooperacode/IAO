"""Runner pequeno e shell-safe para comandos Git.

A engine fornece o mecanismo; flows decidem quais comandos rodar e como interpretar o
resultado.
"""

from __future__ import annotations

import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

# Diretório estável e sempre vazio usado como `core.hooksPath` em todo comando git disparado
# pelo harness: neutraliza hooks do repositório-alvo (pre-commit/post-commit etc.), que de
# outra forma rodariam código arbitrário controlado pelo próprio agente supervisionado.
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
