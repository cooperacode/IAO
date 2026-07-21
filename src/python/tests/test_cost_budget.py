"""Teto de custo (Fase 2): o acumulado de chars de instrução emitida — a única medida que
a engine atesta sozinha — corta o run quando excede o teto. Desligado (0) por padrão —
só o teto de passos vale."""

from pathlib import Path

from harness_engine import harness_config, state_store, task_registry, trace

CONFIG_PATH = "harness.json"

TASKS = {
    "start": lambda _e: "PROMPT_START",
    "classify": lambda _e: "PROMPT_CLASSIFY_0123456789",  # 25 chars por turno
}


def _configure(json_text: str) -> None:
    Path(CONFIG_PATH).write_text(json_text)
    harness_config.reload()


def test_dispatch_proxy_de_chars_corta_quando_o_acumulado_excede():
    _configure('{"maxInstructionChars":30}')

    # 1º turno: acumulado 0 → passa; emite 25 chars.
    first = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    assert first != "stop"

    # 2º turno: acumulado 25 → passa; emite mais 25 (total 50).
    second = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    assert second != "stop"

    # 3º turno: acumulado 50 > 30 → corte por orçamento.
    third = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    assert third == "stop"

    assert trace.load()[-1].outcome == trace.TraceOutcome.BUDGET


def test_dispatch_sem_teto_configurado_nao_corta_por_custo():
    # Default: max_instruction_chars=0 → só o teto de passos governa.
    for _ in range(5):
        result = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
        assert result != "stop"


def test_dispatch_start_zera_o_custo_acumulado():
    _configure('{"maxInstructionChars":30}')

    task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)

    # Novo workflow: reset zera cost_chars junto com o step.
    result = task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    assert result != "stop"
    # O reset zera o acumulado, restando apenas a instrução emitida pelo próprio start.
    assert state_store.load().cost_chars == len("PROMPT_START")
