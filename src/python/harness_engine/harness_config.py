"""Variáveis fixas do harness, externalizadas num `harness.json` na raiz do repo.
Centralizá-las aqui deixa cada flow/ambiente ajustar os tetos sem alterar código.
Ausente ou ilegível → cai nos defaults: config é insumo opcional, não pode derrubar o run.
"""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass, replace
from pathlib import Path

from harness_engine import path_resolver

_FILE_PATH = "harness.json"


@dataclass(frozen=True)
class HarnessConfig:
    max_steps: int
    max_instruction_chars: int
    docs_max_chars: int
    docs_folder: str
    timeout_ms: int

    def to_dict(self) -> dict[str, object]:
        return {
            "maxSteps": self.max_steps,
            "maxInstructionChars": self.max_instruction_chars,
            "docsMaxChars": self.docs_max_chars,
            "docsFolder": self.docs_folder,
            "timeoutMs": self.timeout_ms,
        }


# Teto de passos: impede loop infinito que queimaria tokens indefinidamente.
# max_instruction_chars = 0 desliga o teto de custo (só o de passos vale).
# timeout_ms = 0 desliga a guarda de tempo por passo (mesma convenção do custo).
DEFAULT = HarnessConfig(
    max_steps=12,
    max_instruction_chars=0,
    docs_max_chars=40_000,
    docs_folder="docs",
    timeout_ms=0,
)

_current: HarnessConfig | None = None


def _normalize(config: HarnessConfig) -> HarnessConfig:
    return replace(
        config,
        max_steps=config.max_steps if config.max_steps > 0 else DEFAULT.max_steps,
        max_instruction_chars=max(config.max_instruction_chars, 0),
        docs_max_chars=config.docs_max_chars if config.docs_max_chars > 0 else DEFAULT.docs_max_chars,
        docs_folder=config.docs_folder.strip() if config.docs_folder and config.docs_folder.strip() else DEFAULT.docs_folder,
        timeout_ms=max(config.timeout_ms, 0),
    )


def load() -> HarnessConfig:
    """Relê o `harness.json` do disco; qualquer falha devolve os defaults."""
    try:
        path = Path(path_resolver.resolve(_FILE_PATH))
        if path.exists():
            payload = json.loads(path.read_text())
            config = HarnessConfig(
                max_steps=int(payload.get("maxSteps", 0) or 0),
                max_instruction_chars=int(payload.get("maxInstructionChars", 0) or 0),
                docs_max_chars=int(payload.get("docsMaxChars", 0) or 0),
                docs_folder=str(payload.get("docsFolder") or ""),
                timeout_ms=int(payload.get("timeoutMs", 0) or 0),
            )
            return _normalize(config)
    except Exception as ex:
        print(f"[HarnessConfig] falha ao carregar; usando defaults: {ex}", file=sys.stderr)

    return DEFAULT


def reload() -> HarnessConfig:
    """Força a releitura do `harness.json` — para testes e drivers de longa vida."""
    global _current
    _current = load()
    return _current


def reset() -> None:
    """Limpa o cache sem reler — a próxima `current()` relê sob demanda. Em C# cada
    invocação do harness é um processo novo (o cache dura naturalmente 1 dispatch); num
    processo pytest de longa vida isso não vale de graça, então os testes chamam isto
    (ver conftest.py) para simular a fronteira de processo entre casos."""
    global _current
    _current = None


def current() -> HarnessConfig:
    """Carregada uma vez por processo (cada invocação do harness é um processo novo, então
    'uma vez' = 'por volta do loop'). Leitores estáticos consomem daqui sem precisar
    receber a config por parâmetro."""
    global _current
    if _current is None:
        _current = load()
    return _current
