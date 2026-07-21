"""Cada invocação do harness é um processo novo e sem memória. Este store persiste o
estado acumulado (contador de passos + dados de domínio) em arquivo, para que o
envelope trafegado pelo modelo fique mínimo — economia de tokens: o modelo passa uma
chave, não o estado inteiro, a cada volta do loop.
"""

from __future__ import annotations

import json
import shutil
import sys
from dataclasses import replace
from pathlib import Path

from harness_engine.harness_state import HarnessState

_DIR = ".harness"
_FILE_PATH = ".harness/state.json"

# Estado final congelado do último run concluído. Existe pela mesma razão que
# trace.LAST_RUN_PATH: o `start` de qualquer flow reseta o `state.json` vivo, então a
# avaliação (que checa completude) precisa ler as chaves de domínio de um snapshot
# estável, não do arquivo que seu próprio `start` zerou.
LAST_RUN_STATE_PATH = ".harness/last-run.state.json"

# Estado final congelado do último run de avaliação — caminho próprio, não sobrescreve o
# do refinamento.
LAST_EVALUATION_STATE_PATH = ".harness/last-evaluation.state.json"


def load() -> HarnessState:
    return load_from(_FILE_PATH)


def load_from(path: str) -> HarnessState:
    """Carrega um estado de um caminho arbitrário (ex.: a evidência de um caso do golden set)."""
    try:
        p = Path(path)
        if p.exists():
            payload = json.loads(p.read_text())
            return HarnessState.from_dict(payload)
    except Exception as ex:
        print(f"[StateStore] falha ao carregar: {ex}", file=sys.stderr)

    return HarnessState(step=0, data={})


def save(state: HarnessState) -> None:
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        Path(_FILE_PATH).write_text(json.dumps(state.to_dict(), separators=(",", ":")))
    except Exception as ex:
        print(f"[StateStore] falha ao salvar: {ex}", file=sys.stderr)


def reset() -> None:
    save(HarnessState(step=0, data={}))


def snapshot(destination: str) -> None:
    """Congela o `state.json` vivo no destino — a evidência de completude do run concluído."""
    try:
        if Path(_FILE_PATH).exists():
            Path(_DIR).mkdir(parents=True, exist_ok=True)
            shutil.copyfile(_FILE_PATH, destination)
    except Exception as ex:
        print(f"[StateStore] falha ao congelar: {ex}", file=sys.stderr)


def increment() -> int:
    state = load()
    next_step = state.step + 1
    save(replace(state, step=next_step))
    return next_step


def add_cost(chars: int) -> int:
    """Soma o custo do turno ao acumulado do run e devolve o total — insumo do teto de
    custo em task_registry. Chars de instrução emitida são a única medida: é o que a
    engine consegue atestar sozinha, sem depender de auto-relato do driver."""
    state = load()
    next_state = replace(state, cost_chars=state.cost_chars + chars)
    save(next_state)
    return next_state.cost_chars


def set(key: str, value: str) -> None:
    state = load()
    state.data[key] = value
    save(state)


def get(key: str) -> str | None:
    state = load()
    return state.data.get(key)


def set_context(context: dict[str, str]) -> None:
    """Persiste o contexto do driver capturado no `start` (ver task_registry)."""
    state = load()
    save(replace(state, context=context))


def get_context() -> dict[str, str] | None:
    """Contexto do driver persistido, para prompt_formatter reinjetar em toda saída."""
    return load().context
