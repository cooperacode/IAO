"""Hardening regressions: an error must NEVER turn into a silent "stop", and the step
ceiling has to cut off an infinite loop (token guard)."""

from pathlib import Path

from harness_engine import prompt_formatter, state_store, task_registry, trace
from harness_engine.envelope import Envelope, EnvelopeType

TASKS = {
    "start": lambda _e: "PROMPT_START",
    "classify": lambda e: f"PROMPT_CLASSIFY:{e.args[0] if e and e.args else ''}",
    "finalize": lambda _e: "stop",
}


def _boom(_e):
    raise ValueError("a real bug, not a driver protocol error")


FAULTY_TASKS = {
    "start": lambda _e: "PROMPT_START",
    "boom": _boom,
}


def test_dispatch_comando_registrado_executa_a_action():
    result = task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    assert result == "PROMPT_START"


def test_dispatch_repassa_args_para_a_action():
    result = task_registry.dispatch(['{"type":"tool","value":"classify","args":["Login"]}'], TASKS)

    assert result == "PROMPT_CLASSIFY:Login"


def test_dispatch_finalize_retorna_stop():
    result = task_registry.dispatch(['{"type":"command","value":"finalize"}'], TASKS)

    assert result == "stop"


def test_dispatch_comando_inexistente_retorna_erro_e_nao_stop():
    result = task_registry.dispatch(['{"type":"text","value":"tipo"}'], TASKS)

    assert result.startswith("HARNESS PROTOCOL ERROR")
    assert result != "stop"
    assert "'tipo'" in result


def test_dispatch_json_malformado_retorna_erro_e_nao_stop():
    result = task_registry.dispatch(['{"type":"text","value":'], TASKS)

    assert result.startswith("HARNESS PROTOCOL ERROR")
    assert result != "stop"


def test_dispatch_sem_argumento_retorna_erro_e_nao_stop():
    result = task_registry.dispatch([], TASKS)

    assert result.startswith("HARNESS PROTOCOL ERROR")
    assert result != "stop"


def test_dispatch_mensagem_de_erro_lista_os_comandos_validos():
    result = task_registry.dispatch(['{"type":"text","value":"inexistente"}'], TASKS)

    assert "start" in result
    assert "classify" in result
    assert "finalize" in result


def test_dispatch_start_reinicia_o_contador_de_passos():
    for _ in range(5):
        task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)

    assert state_store.load().step == 5

    task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    # start resets and then counts itself as step 1.
    assert state_store.load().step == 1


def test_dispatch_start_com_should_reset_on_start_falso_nao_trunca_state_nem_trace():
    # "start" also arrives on a RESUME (a fresh session reopening a run in progress) —
    # the flow signals this via should_reset_on_start, and dispatch must not truncate anything.
    for _ in range(3):
        task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    trace.append(99, "handoff", trace.TraceOutcome.INSTRUCTION, 5)

    task_registry.dispatch(
        ['{"type":"text","value":"start"}'], TASKS, should_reset_on_start=lambda: False
    )

    assert state_store.load().step == 4  # 3 previous + "start" itself, no reset
    assert any(e.step == 99 and e.command == "handoff" for e in trace.load())


def test_dispatch_start_sem_predicado_mantem_comportamento_padrao_de_sempre_resetar():
    for _ in range(3):
        task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)

    task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    # backward compatible: no predicate, always resets
    assert state_store.load().step == 1


def test_dispatch_start_com_context_persiste_no_state_store():
    task_registry.dispatch(
        ['{"type":"text","value":"start","context":{"driver":"claude code"}}'], TASKS
    )

    assert state_store.get_context()["driver"] == "claude code"


def test_dispatch_contexto_sobrevive_ao_start_e_e_reinjetado_via_prompt_formatter():
    tasks_with_prompt = {
        "start": lambda _e: prompt_formatter.format(
            "instruction", Envelope(EnvelopeType.COMMAND, "plan", ())
        ),
    }

    result = task_registry.dispatch(
        ['{"type":"text","value":"start","context":{"driver":"claude code"}}'], tasks_with_prompt
    )

    assert '"context":{"driver":"claude code"}' in result


def test_dispatch_ao_exceder_o_teto_forca_stop():
    # Consumes exactly the ceiling — all of these still run normally.
    for _ in range(task_registry.default_max_steps()):
        ok = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
        assert ok != "stop"

    # The next step goes over the ceiling and gets cut off.
    result = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)

    assert result == "stop"


def test_dispatch_action_lanca_excecao_nao_tratada_retorna_stop_e_nao_deixa_vazar():
    # The regression this guards: before the "fault" guard, any exception a task action
    # raised (not just HarnessTimeoutError) propagated all the way out of dispatch,
    # crashing the process instead of a graceful "stop" — see task_registry._resolve.
    result = task_registry.dispatch(['{"type":"tool","value":"boom"}'], FAULTY_TASKS)

    assert result == "stop"


def test_dispatch_action_lanca_excecao_nao_tratada_grava_desfecho_fault_e_marca_terminal():
    task_registry.dispatch(['{"type":"tool","value":"boom"}'], FAULTY_TASKS)

    assert trace.load()[-1].outcome == trace.TraceOutcome.FAULT
    assert state_store.terminal_reason() == "fault"


def test_dispatch_action_lanca_excecao_nao_tratada_run_permanece_terminal_ate_um_start_explicito():
    task_registry.dispatch(['{"type":"tool","value":"boom"}'], FAULTY_TASKS)

    result = task_registry.dispatch(['{"type":"text","value":"start"}'], FAULTY_TASKS)

    assert result == "PROMPT_START"
    assert state_store.terminal_reason() is None


def test_dispatch_loga_entrada_antes_da_action_rodar_e_saida_depois_de_concluir():
    task_registry.dispatch(['{"type":"tool","value":"classify","args":["Login"]}'], TASKS)

    content = Path(".harness", "harness.log").read_text()
    enter_index = content.find("enter 'classify'")
    exit_index = content.find("exit outcome=")

    assert enter_index >= 0, "expected an 'enter' line in harness.log"
    assert exit_index >= 0, "expected an 'exit' line in harness.log"
    assert enter_index < exit_index, "entry must be logged before exit"
