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

from harness_engine.atomic_io import write_text_atomic

_DIR = ".harness"
_FILE_PATH = ".harness/run_config.json"


@dataclass(frozen=True)
class RunConfig:
    """Comando de verificação, diretório-alvo e identidade do run (RFC §6.4), todos
    capturados uma vez pelo `plan`. `run_id` é gerado só num run genuinamente novo — o
    mesmo momento em que `write()` é chamado após `reset()` — e sobrevive a toda retomada
    porque este arquivo não é tocado quando `start` decide que há trabalho pendente (ver
    docstring do módulo)."""

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
    """Grava a configuração do run — mesmo ciclo de vida da feature_list.json (escrita
    pelo `plan`, apagada só quando `start` decide que não há run para retomar)."""
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        write_text_atomic(_FILE_PATH, json.dumps(config.to_dict(), separators=(",", ":")))
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
