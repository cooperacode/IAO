"""Validação contextual (Fase 4): comando certo com VALOR fora da expectativa vira erro
corretivo tipado — nunca "stop" silencioso, nunca persiste conteúdo ruim."""

from harness_engine import envelope_validation, task_registry
from harness_engine.envelope import Envelope

TASKS = {
    "classify": lambda e: f"PROMPT_CLASSIFY:{e.args[0] if e and e.args else ''}",
}

VALIDATORS = {
    "classify": envelope_validation.not_empty("a descrição do item"),
}


def test_dispatch_valor_reprovado_retorna_erro_corretivo_e_nao_executa_a_task():
    result = task_registry.dispatch(['{"type":"tool","value":"classify"}'], TASKS, VALIDATORS)

    assert result.startswith("ERRO")
    assert result != "stop"
    assert "recusado" in result
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
    validator = envelope_validation.min_lines(2, "lista de histórias")

    # Artefatos trafegam como string de uma linha com \n literais (aviso "Compact").
    escaped = Envelope("tool", "acceptance", (r"1. a\n2. b",))
    real = Envelope("tool", "acceptance", ("1. a\n2. b",))
    single = Envelope("tool", "acceptance", ("1. a",))

    assert validator(escaped).ok
    assert validator(real).ok
    assert not validator(single).ok


def test_contains_number_exige_ao_menos_um_digito():
    validator = envelope_validation.contains_number("estimativas")

    assert validator(Envelope("tool", "risks", ("5 pontos",))).ok
    assert not validator(Envelope("tool", "risks", ("sem pontos",))).ok


def test_matches_casa_sem_diferenciar_maiusculas():
    validator = envelope_validation.matches("READY|NOT READY", "veredito do DoR")

    assert validator(Envelope("tool", "finalize", ("Veredito: ready com ressalva",))).ok
    assert not validator(Envelope("tool", "finalize", ("aprovado",))).ok


def test_matches_com_padrao_ancorado_rejeita_conteudo_que_apenas_contem_o_prefixo():
    validator = envelope_validation.matches(r"^(PASS\b|FAIL\b)", "veredito")

    assert validator(Envelope("command", "verify", ("PASS: testes verdes",))).ok
    assert validator(Envelope("command", "verify", ("FAIL: testes vermelhos",))).ok
    assert not validator(Envelope("command", "verify", ("rodei os testes e deu PASS",))).ok


def test_all_of_falha_na_primeira_razao():
    validator = envelope_validation.all_of(
        envelope_validation.not_empty("estimativas"),
        envelope_validation.contains_number("estimativas com pontos"),
    )

    result = validator(Envelope("tool", "risks", ("sem numeros",)))

    assert not result.ok
    assert "número" in result.reason


def test_parse_ignora_campos_desconhecidos():
    # Campos extras (ex.: um "tokens" de driver antigo) não derrubam o parse.
    envelope = Envelope.parse('{"type":"tool","value":"classify","args":["x"],"tokens":1234}')

    assert envelope is not None
    assert envelope.value == "classify"
