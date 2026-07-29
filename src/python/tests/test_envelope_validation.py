"""Contextual validation (Phase 4): the right command with a VALUE outside expectations
becomes a typed corrective error — never a silent "stop", never persists bad content."""

from harness_engine import envelope_validation, task_registry
from harness_engine.envelope import Envelope

TASKS = {
    "classify": lambda e: f"PROMPT_CLASSIFY:{e.args[0] if e and e.args else ''}",
}

VALIDATORS = {
    "classify": envelope_validation.not_empty("the item's description"),
}


def test_dispatch_valor_reprovado_retorna_erro_corretivo_e_nao_executa_a_task():
    result = task_registry.dispatch(['{"type":"tool","value":"classify"}'], TASKS, VALIDATORS)

    assert result.startswith("HARNESS PROTOCOL ERROR")
    assert result != "stop"
    assert "was rejected" in result
    assert "PROMPT_CLASSIFY" not in result


def test_dispatch_valor_aprovado_executa_a_task_normalmente():
    result = task_registry.dispatch(
        ['{"type":"tool","value":"classify","args":["Login"]}'], TASKS, VALIDATORS
    )

    assert result == "PROMPT_CLASSIFY:Login"


def test_dispatch_comando_sem_validador_nao_e_validado():
    result = task_registry.dispatch(['{"type":"tool","value":"classify"}'], TASKS, {})

    assert result.startswith("PROMPT_CLASSIFY")


def test_min_lines_conta_quebras_literais_e_escapadas():
    validator = envelope_validation.min_lines(2, "story list")

    # Artifacts travel as a single-line string with literal \n (the "Compact" notice).
    escaped = Envelope("tool", "acceptance", (r"1. a\n2. b",))
    real = Envelope("tool", "acceptance", ("1. a\n2. b",))
    single = Envelope("tool", "acceptance", ("1. a",))

    assert validator(escaped).ok
    assert validator(real).ok
    assert not validator(single).ok


def test_contains_number_exige_ao_menos_um_digito():
    validator = envelope_validation.contains_number("estimates")

    assert validator(Envelope("tool", "risks", ("5 points",))).ok
    assert not validator(Envelope("tool", "risks", ("no points",))).ok


def test_matches_casa_sem_diferenciar_maiusculas():
    validator = envelope_validation.matches("READY|NOT READY", "DoR verdict")

    assert validator(Envelope("tool", "finalize", ("Verdict: ready with caveat",))).ok
    assert not validator(Envelope("tool", "finalize", ("approved",))).ok


def test_matches_com_padrao_ancorado_rejeita_conteudo_que_apenas_contem_o_prefixo():
    validator = envelope_validation.matches(r"^(PASS\b|FAIL\b)", "verdict")

    assert validator(Envelope("command", "verify", ("PASS: tests green",))).ok
    assert validator(Envelope("command", "verify", ("FAIL: tests red",))).ok
    assert not validator(Envelope("command", "verify", ("ran the tests and got PASS",))).ok


def test_all_of_falha_na_primeira_razao():
    validator = envelope_validation.all_of(
        envelope_validation.not_empty("estimates"),
        envelope_validation.contains_number("estimates with points"),
    )

    result = validator(Envelope("tool", "risks", ("no numbers",)))

    assert not result.ok
    assert "number" in result.reason


def test_parse_ignora_campos_desconhecidos():
    # Extra fields (e.g. a "tokens" field from an old driver) don't break parsing.
    envelope = Envelope.parse('{"type":"tool","value":"classify","args":["x"],"tokens":1234}')

    assert envelope is not None
    assert envelope.value == "classify"
