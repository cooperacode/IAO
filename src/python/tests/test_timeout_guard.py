"""Per-step time guard: a task that hangs (infinite loop in domain logic) is cut off when
it exceeds the ceiling — stderr diagnostic + "stop" on stdout, "timeout" outcome in the
trace. Off (0) by default; enabled via harness.json."""

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
    # Default: timeout_ms=0 → guard disabled; the slow task runs to completion.
    result = task_registry.dispatch(['{"type":"tool","value":"slow"}'], TASKS)

    assert result == "PROMPT_SLOW"
    assert result != "stop"
