"""O lote é o task registry como registry de avaliação: agrega os evaluators
determinísticos sobre um golden set. Puro — testado sem disco nem LLM."""

from harness_engine import batch_evaluator
from harness_engine.golden_case_store import GoldenCase
from harness_engine.harness_state import HarnessState
from harness_engine.trace import TraceEntry, TraceOutcome

HAPPY_PATH = ["start", "classify", "split", "acceptance", "estimate", "risks", "ready_check", "finalize"]
KEYS = ["descricao", "tipo", "veredito"]


def _trace_of(commands: list[str]) -> list[TraceEntry]:
    return [
        TraceEntry(
            i + 1,
            cmd,
            TraceOutcome.STOP if i == len(commands) - 1 else TraceOutcome.INSTRUCTION,
            100,
            "",
        )
        for i, cmd in enumerate(commands)
    ]


def _state_with(*filled_keys: str) -> HarnessState:
    return HarnessState(len(filled_keys), {k: "x" for k in filled_keys})


def test_evaluate_run_perfeito_passa_todas_as_metricas():
    golden = GoldenCase("ok", "caso bom", tuple(HAPPY_PATH), tuple(KEYS))

    result = batch_evaluator.evaluate(golden, _trace_of(HAPPY_PATH), _state_with(*KEYS))

    assert result.passed
    assert any(s.metric == "trajectory" and s.passed for s in result.scores)
    assert any(s.metric == "completeness" and s.passed for s in result.scores)
    assert any(s.metric == "budget" and s.passed for s in result.scores)


def test_evaluate_trajetoria_incompleta_reprova():
    golden = GoldenCase("ruim", "pulou passos", tuple(HAPPY_PATH), tuple(KEYS))

    result = batch_evaluator.evaluate(golden, _trace_of(["start", "classify", "finalize"]), _state_with(*KEYS))

    assert not result.passed
    assert any(s.metric == "trajectory" and not s.passed for s in result.scores)


def test_evaluate_estado_incompleto_reprova():
    golden = GoldenCase("faltou", "sem veredito", tuple(HAPPY_PATH), tuple(KEYS))

    result = batch_evaluator.evaluate(golden, _trace_of(HAPPY_PATH), _state_with("descricao", "tipo"))

    assert not result.passed
    assert any(s.metric == "completeness" and not s.passed for s in result.scores)


def test_evaluate_all_agrega_taxa_de_acerto():
    bom = GoldenCase("bom", "", tuple(HAPPY_PATH), tuple(KEYS))
    ruim = GoldenCase("ruim", "", tuple(HAPPY_PATH), tuple(KEYS))

    batch = batch_evaluator.evaluate_all([
        (bom, _trace_of(HAPPY_PATH), _state_with(*KEYS)),
        (ruim, _trace_of(["start", "classify"]), _state_with(*KEYS)),
    ])

    assert batch.total == 2
    assert batch.passed_count == 1
    assert batch.pass_rate == 0.5


def test_evaluate_all_lote_vazio_pass_rate_zero():
    assert batch_evaluator.evaluate_all([]).pass_rate == 0.0


def test_evaluate_caso_negativo_intencional_que_reprova_nas_metricas_conta_como_ok():
    golden = GoldenCase("negativo", "trajetória ok, conteúdo faltando", tuple(HAPPY_PATH), tuple(KEYS), expect_pass=False)

    result = batch_evaluator.evaluate(golden, _trace_of(HAPPY_PATH), _state_with("descricao", "tipo"))  # faltou veredito

    assert not result.passed  # reprova nas métricas...
    assert result.ok  # ...que é exatamente o comportamento esperado


def test_evaluate_caso_negativo_que_deixa_de_reprovar_conta_como_falha():
    golden = GoldenCase("negativo", "deveria reprovar", tuple(HAPPY_PATH), tuple(KEYS), expect_pass=False)

    result = batch_evaluator.evaluate(golden, _trace_of(HAPPY_PATH), _state_with(*KEYS))  # agora passa em tudo

    assert result.passed
    assert not result.ok  # esperava-se reprovação e não houve → o caso deixou de exercer o que deveria


def test_evaluate_all_caso_negativo_que_reprova_mantem_a_suite_verde():
    bom = GoldenCase("bom", "", tuple(HAPPY_PATH), tuple(KEYS))
    neg = GoldenCase("neg", "", tuple(HAPPY_PATH), tuple(KEYS), expect_pass=False)

    batch = batch_evaluator.evaluate_all([
        (bom, _trace_of(HAPPY_PATH), _state_with(*KEYS)),
        (neg, _trace_of(HAPPY_PATH), _state_with("descricao", "tipo")),
    ])

    assert batch.passed_count == 2  # ambos se comportaram como esperado
    assert batch.pass_rate == 1.0
