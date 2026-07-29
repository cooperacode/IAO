"""Cost ceiling (Phase 2): the accumulated emitted-instruction chars — the only measure
the engine can attest on its own — cuts off the run when it exceeds the ceiling. Off
(0) by default — only the step ceiling applies."""

from pathlib import Path

from harness_engine import harness_config, state_store, task_registry, trace

CONFIG_PATH = "harness.json"

TASKS = {
    "start": lambda _e: "PROMPT_START",
    "classify": lambda _e: "PROMPT_CLASSIFY_0123456789",  # 25 chars per turn
}


def _configure(json_text: str) -> None:
    Path(CONFIG_PATH).write_text(json_text)
    harness_config.reload()


def test_dispatch_proxy_de_chars_corta_quando_o_acumulado_excede():
    _configure('{"maxInstructionChars":30}')

    # 1st turn: accumulated 0 → passes; emits 25 chars.
    first = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    assert first != "stop"

    # 2nd turn: accumulated 25 → passes; emits 25 more (total 50).
    second = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    assert second != "stop"

    # 3rd turn: accumulated 50 > 30 → cut off by budget.
    third = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    assert third == "stop"

    assert trace.load()[-1].outcome == trace.TraceOutcome.BUDGET


def test_dispatch_sem_teto_configurado_nao_corta_por_custo():
    # Default: max_instruction_chars=0 → only the step ceiling governs.
    for _ in range(5):
        result = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
        assert result != "stop"


def test_dispatch_start_zera_o_custo_acumulado():
    _configure('{"maxInstructionChars":30}')

    task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)

    # New workflow: reset zeros out cost_chars along with step.
    result = task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    assert result != "stop"
    # The reset zeros the accumulator, leaving only the instruction emitted by start itself.
    assert state_store.load().cost_chars == len("PROMPT_START")
