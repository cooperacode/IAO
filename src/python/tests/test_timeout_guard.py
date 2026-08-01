"""Per-step time guard: a task that hangs (infinite loop in domain logic) is cut off when
it exceeds the ceiling — stderr diagnostic + "stop" on stdout, "timeout" outcome in the
trace. Off (0) by default; enabled via harness.json."""

import time
from pathlib import Path

from harness_engine import harness_config, state_store, task_registry, trace

CONFIG_PATH = "harness.json"


def _slow(_e):
    time.sleep(0.5)
    return "PROMPT_SLOW"


TASKS = {
    "start": lambda _e: "PROMPT_START",
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

    # Non-start commands remain terminal even if the workspace config is changed.
    Path(CONFIG_PATH).write_text('{"timeoutMs":0}')
    harness_config.reload()
    assert task_registry.dispatch(['{"type":"tool","value":"fast"}'], TASKS) == "stop"

    # An explicit start clears only the recoverable timeout latch.
    assert task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS) == "PROMPT_START"
    assert state_store.terminal_reason() is None
    assert task_registry.dispatch(['{"type":"tool","value":"fast"}'], TASKS) == "PROMPT_FAST"


def test_dispatch_task_rapida_dentro_do_teto_executa_normalmente():
    _configure('{"timeoutMs":50}')

    result = task_registry.dispatch(['{"type":"tool","value":"fast"}'], TASKS)

    assert result == "PROMPT_FAST"
    assert trace.load()[-1].outcome == trace.TraceOutcome.INSTRUCTION


def test_dispatch_sem_teto_configurado_nao_corta_task_lenta():
    # Default timeout is enabled, but this task completes within the 30-second default.
    result = task_registry.dispatch(['{"type":"tool","value":"slow"}'], TASKS)

    assert result == "PROMPT_SLOW"
    assert result != "stop"
