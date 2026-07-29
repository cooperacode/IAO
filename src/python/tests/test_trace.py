"""The trace is the sequence of commands that state_store doesn't keep (it overwrites
the state). Without it there's no Trajectory Evaluation or per-step cost Telemetry."""

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

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


# --- hash-chain (prevHash) ----------------------------------------------------------


def _raw_lines() -> list[str]:
    return [line for line in Path(".harness/trace.jsonl").read_text().split("\n") if line.strip()]


def test_primeira_entrada_grava_prev_hash_de_genese():
    trace.append(1, "start", trace.TraceOutcome.INSTRUCTION, 10)

    entries = trace.load()

    assert entries[0].prev_hash == "0" * 64


def test_prev_hash_da_segunda_entrada_bate_com_sha256_da_primeira_linha_serializada():
    trace.append(1, "start", trace.TraceOutcome.INSTRUCTION, 10)
    trace.append(2, "classify", trace.TraceOutcome.INSTRUCTION, 20)

    raw = _raw_lines()
    assert len(raw) == 2

    expected = hashlib.sha256(raw[0].encode("utf-8")).hexdigest()
    entries = trace.load()
    assert entries[1].prev_hash == expected


def test_hash_chain_encadeia_tres_entradas_em_sequencia():
    trace.append(1, "start", trace.TraceOutcome.INSTRUCTION, 10)
    trace.append(2, "classify", trace.TraceOutcome.INSTRUCTION, 20)
    trace.append(3, "finalize", trace.TraceOutcome.STOP, 5)

    raw = _raw_lines()
    assert len(raw) == 3

    entries = trace.load()
    assert entries[0].prev_hash == "0" * 64
    assert entries[1].prev_hash == hashlib.sha256(raw[0].encode("utf-8")).hexdigest()
    assert entries[2].prev_hash == hashlib.sha256(raw[1].encode("utf-8")).hexdigest()


def test_prev_hash_e_gravado_como_prevhash_no_json_serializado():
    trace.append(1, "start", trace.TraceOutcome.INSTRUCTION, 10)

    raw = _raw_lines()
    payload = json.loads(raw[0])

    assert "prevHash" in payload
    assert payload["prevHash"] == "0" * 64


def test_reset_reinicia_a_cadeia_na_genese():
    trace.append(1, "start", trace.TraceOutcome.INSTRUCTION, 10)
    trace.append(2, "classify", trace.TraceOutcome.INSTRUCTION, 20)

    trace.reset()
    trace.append(1, "start", trace.TraceOutcome.INSTRUCTION, 10)

    assert trace.load()[0].prev_hash == "0" * 64


def test_from_dict_sem_prev_hash_usa_default_vazio():
    # Compatibility with a trace.jsonl written before this field existed.
    legacy = {"step": 1, "command": "start", "outcome": "instruction", "instructionChars": 10, "timestamp": "x"}

    entry = trace.TraceEntry.from_dict(legacy)

    assert entry.prev_hash == ""
