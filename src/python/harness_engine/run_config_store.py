"""Persiste `verify_cmd`/`target_dir` (capturados uma vez pelo `plan`) em
`.harness/run_config.json` — fora de `state.json` de propósito. task_registry reseta
`state.json` incondicionalmente a cada `start`, antes de qualquer código de domínio
rodar; um run retomado ainda precisa desses dois valores para `smoke`/`verify`
funcionarem, então eles têm que sobreviver a esse reset.
"""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass
from pathlib import Path

_DIR = ".harness"
_FILE_PATH = ".harness/run_config.json"


@dataclass(frozen=True)
class RunConfig:
    """Comando de verificação e diretório-alvo capturados pelo `plan`."""

    verify_cmd: str = ""
    target_dir: str = "."

    def to_dict(self) -> dict[str, object]:
        return {"verifyCmd": self.verify_cmd, "targetDir": self.target_dir}

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "RunConfig":
        return RunConfig(
            verify_cmd=str(payload.get("verifyCmd") or ""),
            target_dir=str(payload.get("targetDir") or "."),
        )


def write(config: RunConfig) -> None:
    """Grava a configuração do run — mesmo ciclo de vida da feature_list.json (escrita
    pelo `plan`, apagada só quando `start` decide que não há run para retomar)."""
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        Path(_FILE_PATH).write_text(json.dumps(config.to_dict(), separators=(",", ":")))
    except Exception as ex:
        print(f"[RunConfigStore] falha ao gravar: {ex}", file=sys.stderr)


def load() -> RunConfig:
    """Lê a configuração persistida, ou os defaults se nada foi gravado ainda."""
    try:
        p = Path(_FILE_PATH)
        if p.exists():
            return RunConfig.from_dict(json.loads(p.read_text()))
    except Exception as ex:
        print(f"[RunConfigStore] falha ao carregar: {ex}", file=sys.stderr)

    return RunConfig()


def reset() -> None:
    """Apaga num run genuinamente novo — em par com feature_store.reset()."""
    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        print(f"[RunConfigStore] falha ao limpar: {ex}", file=sys.stderr)
