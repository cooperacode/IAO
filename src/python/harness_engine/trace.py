"""Grava uma linha por volta do loop em `.harness/trace.jsonl`. É a base tanto da
telemetria quanto do evaluator de trajetória: state_store guarda só o estado final —
sobrescreve `data` a cada passo —, então sem esta sequência gravada não há como avaliar
o caminho que o agente percorreu.

Custo: zero token e uma escrita append por invocação.
"""

from __future__ import annotations

import json
import shutil
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

_DIR = ".harness"
_FILE_PATH = ".harness/trace.jsonl"

# Trajetória congelada do último run que terminou em `stop`. harness_host grava aqui ao
# concluir o flow produtor, para que outro flow (a avaliação) leia a evidência mesmo
# depois de resetar o `trace.jsonl` vivo no próprio `start`.
LAST_RUN_PATH = ".harness/last-run.trace.jsonl"

# Trajetória congelada do último run de avaliação. Caminho próprio para que a avaliação
# (que também termina em `stop`) não sobrescreva a evidência do run em LAST_RUN_PATH.
LAST_EVALUATION_PATH = ".harness/last-evaluation.trace.jsonl"


class TraceOutcome:
    """Desfechos possíveis de um passo, gravados em `TraceEntry.outcome`."""

    INSTRUCTION = "instruction"  # seguiu para o próximo passo
    STOP = "stop"                # término normal do flow
    ERROR = "error"              # erro tipado devolvido ao driver
    BUDGET = "budget"            # corte pelo teto de passos
    TIMEOUT = "timeout"          # corte pelo teto de tempo por passo


@dataclass(frozen=True)
class TraceEntry:
    """Uma volta do loop: passo, comando recebido, desfecho, custo (chars da instrução
    emitida) e horário de gravação."""

    step: int
    command: str
    outcome: str
    instruction_chars: int
    timestamp: str  # ISO 8601 com offset, gravado como string (paridade com o wire JSON)

    def to_dict(self) -> dict[str, object]:
        return {
            "step": self.step,
            "command": self.command,
            "outcome": self.outcome,
            "instructionChars": self.instruction_chars,
            "timestamp": self.timestamp,
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "TraceEntry":
        return TraceEntry(
            step=int(payload["step"]),
            command=str(payload["command"]),
            outcome=str(payload["outcome"]),
            instruction_chars=int(payload["instructionChars"]),
            timestamp=str(payload["timestamp"]),
        )


def reset() -> None:
    """Trunca o trace no início de um novo workflow (junto do state_store.reset)."""
    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        print(f"[Trace] falha ao limpar: {ex}", file=sys.stderr)


def append(step: int, command: str, outcome: str, instruction_chars: int) -> None:
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        entry = TraceEntry(step, command, outcome, instruction_chars, _now_iso())
        with open(_FILE_PATH, "a") as f:
            f.write(json.dumps(entry.to_dict(), separators=(",", ":")) + "\n")
    except Exception as ex:
        print(f"[Trace] falha ao gravar: {ex}", file=sys.stderr)


def snapshot(destination: str) -> None:
    """Congela o trace vivo no caminho de destino — a evidência do run concluído."""
    try:
        if Path(_FILE_PATH).exists():
            Path(_DIR).mkdir(parents=True, exist_ok=True)
            shutil.copyfile(_FILE_PATH, destination)
    except Exception as ex:
        print(f"[Trace] falha ao congelar: {ex}", file=sys.stderr)


def load() -> list[TraceEntry]:
    """Relê o trace vivo na ordem em que foi gravado."""
    return load_from(_FILE_PATH)


def load_from(path: str) -> list[TraceEntry]:
    """Relê um trace de um caminho arbitrário — insumo dos evaluators (ex.: o snapshot)."""
    try:
        p = Path(path)
        if not p.exists():
            return []

        entries: list[TraceEntry] = []
        for line in p.read_text().splitlines():
            if not line.strip():
                continue
            entries.append(TraceEntry.from_dict(json.loads(line)))
        return entries
    except Exception as ex:
        print(f"[Trace] falha ao carregar: {ex}", file=sys.stderr)
        return []


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()
