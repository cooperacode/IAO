"""The per-invocation `max_steps` override: a long-running flow (e.g. Development)
raises the global ceiling only for its own process, without touching the shared
`harness.json`. With no override, the config's ceiling applies."""

from harness_engine import task_registry

TASKS = {"ping": lambda _e: "PONG"}


def _ping(max_steps: int | None) -> str:
    return task_registry.dispatch(['{"type":"tool","value":"ping"}'], TASKS, None, max_steps)


def test_sem_override_corta_no_teto_global():
    last = ""
    for _ in range(task_registry.default_max_steps() + 1):
        last = _ping(None)

    assert last == "stop"  # step max_steps+1 is cut off by the global guard


def test_com_override_maior_nao_corta_alem_do_teto_global():
    last = ""
    for _ in range(task_registry.default_max_steps() + 5):
        last = _ping(task_registry.default_max_steps() + 20)

    assert last != "stop"  # the override gave the slack the global ceiling wouldn't
