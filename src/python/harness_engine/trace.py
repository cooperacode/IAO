"""Grava uma linha por volta do loop em `.harness/trace.jsonl`. É a base tanto da
telemetria quanto do evaluator de trajetória: state_store guarda só o estado final —
sobrescreve `data` a cada passo —, então sem esta sequência gravada não há como avaliar
o caminho que o agente percorreu.

Custo: zero token e uma escrita append por invocação.
"""

from __future__ import annotations

import hashlib
import json
import shutil
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

_DIR = ".harness"
_FILE_PATH = ".harness/trace.jsonl"

# Hash de gênese: usado como `prev_hash` da primeira entrada de um trace (arquivo ausente
# ou vazio) — 64 zeros, o mesmo comprimento de um digest sha256 hex, para que todo
# `prevHash` gravado tenha formato uniforme independente da posição na cadeia.
_GENESIS_HASH = "0" * 64

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
    emitida), horário de gravação e o hash da linha anterior da cadeia (`prev_hash`) —
    encadeamento que torna uma edição/remoção retroativa de uma entrada detectável (a
    entrada seguinte deixa de bater com o hash gravado). `prev_hash` tem default "" para
    que traces antigos, gravados antes deste campo existir, continuem desserializando.

    `label` é a etiqueta opcional e agnóstica de domínio (ex.: "feature:3") que resolve a
    mesma dor do state_store: `step` é um contador global do run inteiro, não identifica a
    QUE unidade de trabalho o passo pertence. trace só carrega o valor — quem decide o que
    ele significa é o flow (ver flows_development.tasks.pick). Default "" pelo mesmo motivo
    de prev_hash: paridade com traces gravados antes deste campo existir."""

    step: int
    command: str
    outcome: str
    instruction_chars: int
    timestamp: str  # ISO 8601 com offset, gravado como string (paridade com o wire JSON)
    prev_hash: str = ""
    label: str = ""

    def to_dict(self) -> dict[str, object]:
        return {
            "step": self.step,
            "command": self.command,
            "outcome": self.outcome,
            "instructionChars": self.instruction_chars,
            "timestamp": self.timestamp,
            "prevHash": self.prev_hash,
            "label": self.label,
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "TraceEntry":
        return TraceEntry(
            step=int(payload["step"]),
            command=str(payload["command"]),
            outcome=str(payload["outcome"]),
            instruction_chars=int(payload["instructionChars"]),
            timestamp=str(payload["timestamp"]),
            prev_hash=str(payload.get("prevHash") or ""),
            label=str(payload.get("label") or ""),
        )


def reset() -> None:
    """Trunca o trace no início de um novo workflow (junto do state_store.reset)."""
    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        print(f"[Trace] falha ao limpar: {ex}", file=sys.stderr)


def append(step: int, command: str, outcome: str, instruction_chars: int, label: str = "") -> None:
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        prev_hash = _last_entry_hash()
        entry = TraceEntry(step, command, outcome, instruction_chars, _now_iso(), prev_hash, label)
        line = json.dumps(entry.to_dict(), separators=(",", ":")) + "\n"
        with open(_FILE_PATH, "a") as f:
            f.write(line)  # uma única write() — o evento inteiro é atômico ao nível de linha
    except Exception as ex:
        print(f"[Trace] falha ao gravar: {ex}", file=sys.stderr)


def _last_entry_hash() -> str:
    """sha256 hex da última linha não-vazia gravada, ou o hash de gênese se o trace
    estiver ausente/vazio (inclui logo após `reset()`) — a raiz da cadeia."""
    try:
        p = Path(_FILE_PATH)
        if not p.exists():
            return _GENESIS_HASH

        last_line = ""
        for line in p.read_text().split("\n"):
            if line.strip():
                last_line = line

        if not last_line:
            return _GENESIS_HASH

        return hashlib.sha256(last_line.encode("utf-8")).hexdigest()
    except Exception as ex:
        print(f"[Trace] falha ao calcular prevHash: {ex}", file=sys.stderr)
        return _GENESIS_HASH


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
