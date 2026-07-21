"""Guarda de tempo por passo: uma task que trava (loop infinito na lógica de domínio) é
cortada ao exceder o teto — diagnóstico no stderr + "stop" no stdout, desfecho "timeout"
no trace. Desligada (0) por padrão; ligada via harness.json."""

import time
from pathlib import Path

from harness_engine import harness_config, task_registry, trace

CONFIG_PATH = "harness.json"


def _slow(_e):
    time.sleep(0.5)
    return "PROMPT_SLOW"


TASKS = {
    "fast": lambda _e: "PROMPT_FAST",
    "slow": _slow,
}


def _configure(json_text: str) -> None:
    Path(CONFIG_PATH).write_text(json_text)
    harness_config.reload()


def test_dispatch_task_lenta_alem_do_teto_corta_com_timeout():
    _configure('{"timeoutMs":50}')

    result = task_registry.dispatch(['{"type":"tool","value":"slow"}'], TASKS)

    assert result == "stop"
    assert trace.load()[-1].outcome == trace.TraceOutcome.TIMEOUT


def test_dispatch_task_rapida_dentro_do_teto_executa_normalmente():
    _configure('{"timeoutMs":50}')

    result = task_registry.dispatch(['{"type":"tool","value":"fast"}'], TASKS)

    assert result == "PROMPT_FAST"
    assert trace.load()[-1].outcome == trace.TraceOutcome.INSTRUCTION


def test_dispatch_sem_teto_configurado_nao_corta_task_lenta():
    # Default: timeout_ms=0 → guarda desligada; a task lenta roda até o fim.
    result = task_registry.dispatch(['{"type":"tool","value":"slow"}'], TASKS)

    assert result == "PROMPT_SLOW"
    assert result != "stop"
