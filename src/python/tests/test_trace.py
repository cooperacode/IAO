"""O trace é a sequência de comandos que state_store não guarda (ele sobrescreve o
estado). Sem ele não há Trajectory Evaluation nem Telemetria de custo por passo."""

from datetime import datetime, timezone

from harness_engine import state_store, task_registry, trace

TASKS = {
    "start": lambda _e: "PROMPT_START",
    "classify": lambda e: f"PROMPT_CLASSIFY:{e.args[0] if e and e.args else ''}",
    "finalize": lambda _e: "stop",
}


def test_append_e_load_fazem_roundtrip_na_ordem_de_gravacao():
    before = datetime.now(timezone.utc)
    trace.append(1, "start", trace.TraceOutcome.INSTRUCTION, 42)
    trace.append(2, "classify", trace.TraceOutcome.INSTRUCTION, 99)
    after = datetime.now(timezone.utc)

    entries = trace.load()

    assert len(entries) == 2
    assert (entries[0].step, entries[0].command, entries[0].outcome, entries[0].instruction_chars) == (
        1, "start", trace.TraceOutcome.INSTRUCTION, 42,
    )
    assert (entries[1].step, entries[1].command, entries[1].outcome, entries[1].instruction_chars) == (
        2, "classify", trace.TraceOutcome.INSTRUCTION, 99,
    )
    ts0 = datetime.fromisoformat(entries[0].timestamp)
    ts1 = datetime.fromisoformat(entries[1].timestamp)
    assert before <= ts0 <= after
    assert before <= ts1 <= after


def test_load_sem_arquivo_retorna_vazio():
    assert trace.load() == []


def test_dispatch_grava_o_comando_e_o_desfecho_de_cada_passo():
    task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)
    task_registry.dispatch(['{"type":"tool","value":"classify","args":["Login"]}'], TASKS)
    task_registry.dispatch(['{"type":"command","value":"finalize"}'], TASKS)

    entries = trace.load()

    assert [e.command for e in entries] == ["start", "classify", "finalize"]
    assert [e.outcome for e in entries] == [
        trace.TraceOutcome.INSTRUCTION,
        trace.TraceOutcome.INSTRUCTION,
        trace.TraceOutcome.STOP,
    ]


def test_dispatch_start_trunca_o_trace_anterior():
    task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
    assert len(trace.load()) == 2

    task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    entries = trace.load()
    assert len(entries) == 1
    assert entries[0].command == "start"
    assert entries[0].step == 1


def test_dispatch_json_malformado_grava_comando_unparsed_com_desfecho_error():
    task_registry.dispatch(['{"type":"text","value":'], TASKS)

    entries = trace.load()
    assert len(entries) == 1
    assert entries[0].command == "(unparsed)"
    assert entries[0].outcome == trace.TraceOutcome.ERROR


def test_dispatch_ao_exceder_o_teto_grava_desfecho_budget():
    for _ in range(task_registry.default_max_steps()):
        task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)

    task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)

    last = trace.load()[-1]
    assert last.outcome == trace.TraceOutcome.BUDGET
    assert last.step == task_registry.default_max_steps() + 1
