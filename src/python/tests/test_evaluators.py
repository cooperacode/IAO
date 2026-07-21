"""Evaluators determinísticos são funções puras — testadas sem tocar disco nem LLM. São
o portão barato antes do juiz-LLM."""

import pytest

from harness_engine import evaluators
from harness_engine.harness_state import HarnessState
from harness_engine.trace import TraceEntry, TraceOutcome


@pytest.mark.parametrize(
    "expected,actual,value",
    [
        ("Bug", "Bug", 1.0),
        ("Bug", "  Bug  ", 1.0),
        ("Bug", "Épico", 0.0),
    ],
)
def test_exact_match_normaliza_espacos_e_compara_conteudo(expected, actual, value):
    assert evaluators.exact_match(expected, actual).value == value


def test_matches_regex_avalia_o_padrao():
    assert evaluators.matches_regex(r"^\d+\s*pts$", "13 pts").passed
    assert not evaluators.matches_regex(r"^\d+\s*pts$", "treze").passed


def test_trajectory_caminho_identico_pontua_cheio():
    esperado = ["start", "classify", "finalize"]

    score = evaluators.trajectory(esperado, ["start", "classify", "finalize"])

    assert score.passed
    assert score.value == 1.0


def test_trajectory_diverge_no_meio_conta_so_o_prefixo_em_ordem():
    esperado = ["start", "classify", "split", "finalize"]

    # Acerta start+classify, depois pula direto para finalize (fora de ordem).
    score = evaluators.trajectory(esperado, ["start", "classify", "finalize"])

    assert score.value == 0.5  # 2 de 4
    assert not score.passed


def test_trajectory_esperado_vazio_pontua_cheio():
    assert evaluators.trajectory([], []).passed


def test_completeness_conta_chaves_preenchidas():
    state = HarnessState(3, {
        "descricao": "Login",
        "tipo": "Feature",
        "historias": "   ",  # em branco não conta
    })

    score = evaluators.completeness(state, ["descricao", "tipo", "historias"])

    assert score.value == pytest.approx(2.0 / 3.0)
    assert not score.passed


def test_step_budget_concluiu_com_stop_passa():
    trace_entries = [
        TraceEntry(1, "start", TraceOutcome.INSTRUCTION, 100, ""),
        TraceEntry(2, "finalize", TraceOutcome.STOP, 4, ""),
    ]

    assert evaluators.step_budget(trace_entries).passed


def test_step_budget_cortado_pelo_teto_falha():
    trace_entries = [
        TraceEntry(1, "classify", TraceOutcome.INSTRUCTION, 100, ""),
        TraceEntry(13, "classify", TraceOutcome.BUDGET, 4, ""),
    ]

    assert not evaluators.step_budget(trace_entries).passed


def test_commands_of_ignora_voltas_de_erro_por_padrao():
    trace_entries = [
        TraceEntry(1, "start", TraceOutcome.INSTRUCTION, 100, ""),
        TraceEntry(2, "(unparsed)", TraceOutcome.ERROR, 200, ""),
        TraceEntry(3, "classify", TraceOutcome.INSTRUCTION, 150, ""),
    ]

    assert evaluators.commands_of(trace_entries) == ["start", "classify"]
    assert evaluators.commands_of(trace_entries, include_errors=True) == [
        "start", "(unparsed)", "classify",
    ]
