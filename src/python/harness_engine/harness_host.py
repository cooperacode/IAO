"""Ponto de entrada reutilizável de um flow. Um novo domínio só precisa definir suas
tasks e chamar `run` — toda a orquestração (dispatch, guardas, transporte) fica aqui.
"""

from __future__ import annotations

from typing import Mapping

from harness_engine import state_store, task_registry, trace
from harness_engine.task_registry import Action, Validator


def run(
    args: list[str],
    tasks: Mapping[str, Action],
    trace_snapshot_path: str = trace.LAST_RUN_PATH,
    state_snapshot_path: str = state_store.LAST_RUN_STATE_PATH,
    validators: Mapping[str, Validator] | None = None,
    max_steps: int | None = None,
) -> int:
    result = task_registry.dispatch(args, tasks, validators, max_steps)

    # Run concluído: congela trajetória E estado final como evidência para a avaliação
    # posterior, antes que um próximo flow resete o trace e o state vivos. Cada flow
    # publica no SEU caminho, para que a avaliação não sobrescreva o que ela mesma consome.
    if result == "stop":
        trace.snapshot(trace_snapshot_path)
        state_store.snapshot(state_snapshot_path)

    # Único ponto que escreve no stdout — o canal de transporte do harness.
    print(result)
    return 0
