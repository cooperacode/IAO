"""The batch is the task registry as an evaluation registry: aggregates the
deterministic evaluators over a golden set. Pure — tested without disk or an LLM."""

from harness_engine import batch_evaluator
from harness_engine.golden_case_store import GoldenCase
from harness_engine.harness_state import HarnessState
from harness_engine.trace import TraceEntry, TraceOutcome

HAPPY_PATH = ["start", "classify", "split", "acceptance", "estimate", "risks", "ready_check", "finalize"]
KEYS = ["description", "type", "verdict"]


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
    golden = GoldenCase("ok", "good case", tuple(HAPPY_PATH), tuple(KEYS))

    result = batch_evaluator.evaluate(golden, _trace_of(HAPPY_PATH), _state_with(*KEYS))

    assert result.passed
    assert any(s.metric == "trajectory" and s.passed for s in result.scores)
    assert any(s.metric == "completeness" and s.passed for s in result.scores)
    assert any(s.metric == "budget" and s.passed for s in result.scores)


def test_evaluate_trajetoria_incompleta_reprova():
    golden = GoldenCase("ruim", "skipped steps", tuple(HAPPY_PATH), tuple(KEYS))

    result = batch_evaluator.evaluate(golden, _trace_of(["start", "classify", "finalize"]), _state_with(*KEYS))

    assert not result.passed
    assert any(s.metric == "trajectory" and not s.passed for s in result.scores)


def test_evaluate_estado_incompleto_reprova():
    golden = GoldenCase("faltou", "no verdict", tuple(HAPPY_PATH), tuple(KEYS))

    result = batch_evaluator.evaluate(golden, _trace_of(HAPPY_PATH), _state_with("description", "type"))

    assert not result.passed
    assert any(s.metric == "completeness" and not s.passed for s in result.scores)


def test_evaluate_all_agrega_taxa_de_acerto():
    good = GoldenCase("bom", "", tuple(HAPPY_PATH), tuple(KEYS))
    bad = GoldenCase("ruim", "", tuple(HAPPY_PATH), tuple(KEYS))

    batch = batch_evaluator.evaluate_all([
        (good, _trace_of(HAPPY_PATH), _state_with(*KEYS)),
        (bad, _trace_of(["start", "classify"]), _state_with(*KEYS)),
    ])

    assert batch.total == 2
    assert batch.passed_count == 1
    assert batch.pass_rate == 0.5


def test_evaluate_all_lote_vazio_pass_rate_zero():
    assert batch_evaluator.evaluate_all([]).pass_rate == 0.0


def test_evaluate_caso_negativo_intencional_que_reprova_nas_metricas_conta_como_ok():
    golden = GoldenCase("negativo", "trajectory ok, missing content", tuple(HAPPY_PATH), tuple(KEYS), expect_pass=False)

    result = batch_evaluator.evaluate(golden, _trace_of(HAPPY_PATH), _state_with("description", "type"))  # missing verdict

    assert not result.passed  # fails the metrics...
    assert result.ok  # ...which is exactly the expected behavior


def test_evaluate_caso_negativo_que_deixa_de_reprovar_conta_como_falha():
    golden = GoldenCase("negativo", "should fail", tuple(HAPPY_PATH), tuple(KEYS), expect_pass=False)

    result = batch_evaluator.evaluate(golden, _trace_of(HAPPY_PATH), _state_with(*KEYS))  # now passes everything

    assert result.passed
    assert not result.ok  # a failure was expected and didn't happen → the case stopped exercising what it should


def test_evaluate_all_caso_negativo_que_reprova_mantem_a_suite_verde():
    good = GoldenCase("bom", "", tuple(HAPPY_PATH), tuple(KEYS))
    neg = GoldenCase("neg", "", tuple(HAPPY_PATH), tuple(KEYS), expect_pass=False)

    batch = batch_evaluator.evaluate_all([
        (good, _trace_of(HAPPY_PATH), _state_with(*KEYS)),
        (neg, _trace_of(HAPPY_PATH), _state_with("description", "type")),
    ])

    assert batch.passed_count == 2  # both behaved as expected
    assert batch.pass_rate == 1.0
