"""O override de `max_steps` por invocação: um flow long-running (ex.: Development)
levanta o teto global só para o seu processo, sem tocar o `harness.json` compartilhado.
Sem override, vale o teto do config."""

from harness_engine import task_registry

TASKS = {"ping": lambda _e: "PONG"}


def _ping(max_steps: int | None) -> str:
    return task_registry.dispatch(['{"type":"tool","value":"ping"}'], TASKS, None, max_steps)


def test_sem_override_corta_no_teto_global():
    last = ""
    for _ in range(task_registry.default_max_steps() + 1):
        last = _ping(None)

    assert last == "stop"  # o passo max_steps+1 é cortado pela guarda global


def test_com_override_maior_nao_corta_alem_do_teto_global():
    last = ""
    for _ in range(task_registry.default_max_steps() + 5):
        last = _ping(task_registry.default_max_steps() + 20)

    assert last != "stop"  # o override deu a folga que o global não daria
