"""Store- and evaluator-level coverage for the Replan feature. See
test_development_flow.py for the two end-to-end scenarios (a revision applied via
tasks.replan, and the 3rd-consecutive-verify-failure escalation) — these exercise
plan_revision_evaluator's deterministic gate and the supporting stores directly, without
going through tasks.py.
"""

from __future__ import annotations

from dataclasses import replace
from pathlib import Path

import pytest

from harness_engine import feature_store, plan_observation_store, plan_revision_evaluator, plan_revision_store
from harness_engine.feature_store import Feature, ImplementationContext, PlanRevision
from harness_engine.plan_revision_evaluator import PlanRevisionVerdict


@pytest.fixture
def observation():
    return plan_observation_store.append(
        "verification_failure", 2, "API verification failed repeatedly.", "exit 1"
    )


def _feature_with_coverage(id_: int, title: str, priority: int, passes: bool = False) -> Feature:
    return Feature(
        id_, title, priority, passes,
        references=("RF-001",),
        implementation_context=ImplementationContext(acceptance=("returns HTTP 401",)),
    )


def _revision(observation_id: str, *features: Feature) -> PlanRevision:
    return PlanRevision(
        "new evidence requires a global dependency",
        ("keep the stub", "introduce the dependency; selected because it preserves behavior"),
        features,
        (observation_id,),
    )


# --- plan_revision_evaluator.evaluate ------------------------------------------------


def test_evaluate_aprova_mudanca_rastreavel_que_preserva_cobertura(observation):
    current = [_feature_with_coverage(1, "API", 2)]
    revision = _revision(
        observation.id,
        replace(_feature_with_coverage(1, "API", 2), depends_on=(2,)),
        Feature(2, "Authentication", 1, False),
    )

    result = plan_revision_evaluator.evaluate(current, revision, plan_observation_store.load(), 10, 80, 8)

    assert result.verdict == PlanRevisionVerdict.APPROVE
    assert result.errors == ()
    assert result.diff.added == (2,)
    assert result.diff.modified == (1,)


def test_evaluate_rejeita_perda_de_referencia_e_acceptance(observation):
    current = [_feature_with_coverage(1, "API", 2)]
    revision = _revision(observation.id, Feature(1, "API reduced", 2, False))

    result = plan_revision_evaluator.evaluate(current, revision, plan_observation_store.load(), 10, 80, 8)

    assert result.verdict == PlanRevisionVerdict.REJECT
    codes = {e.code for e in result.errors}
    assert "REQUIREMENT_COVERAGE_REMOVED" in codes
    assert "ACCEPTANCE_REMOVED" in codes


def test_evaluate_rejeita_observacao_desconhecida_e_plano_sem_mudanca(observation):
    current = [Feature(1, "API", 1, False)]
    revision = PlanRevision("retry", ("A", "B"), tuple(current), ("OBS-999",))

    result = plan_revision_evaluator.evaluate(current, revision, plan_observation_store.load(), 10, 80, 8)

    codes = {e.code for e in result.errors}
    assert "OBSERVATION_UNKNOWN" in codes
    assert "PLAN_UNCHANGED" in codes


def test_evaluate_aprova_com_aviso_quando_orcamento_pode_ser_insuficiente(observation):
    current = [Feature(1, "API", 1, False)]
    revision = _revision(observation.id, Feature(1, "API", 2, False), Feature(2, "Auth", 1, False))

    result = plan_revision_evaluator.evaluate(
        current, revision, plan_observation_store.load(), 10, remaining_steps=4, steps_per_feature=8,
    )

    assert result.verdict == PlanRevisionVerdict.APPROVE_WITH_WARNINGS
    assert any(w.code == "BUDGET_RISK" for w in result.warnings)


def test_evaluate_rejeita_plano_ja_aplicado_no_historico(observation):
    current = [Feature(1, "API", 2, False)]
    revision = _revision(observation.id, Feature(1, "API", 2, False, (2,)), Feature(2, "Auth", 1, False))
    first = plan_revision_evaluator.evaluate(current, revision, plan_observation_store.load(), 10, 80, 8)
    plan_revision_store.record(revision, list(revision.features), first)

    repeated = plan_revision_evaluator.evaluate(current, revision, plan_observation_store.load(), 10, 80, 8)

    assert any(e.code == "PLAN_REPEATED" for e in repeated.errors)


# --- feature_store.apply_revision -----------------------------------------------------


def test_apply_revision_rejeita_remocao_de_feature_passada():
    feature_store.write([Feature(1, "API", 1, True), Feature(2, "Auth", 2, False)])
    revision = PlanRevision("drop stale feature", ("keep", "drop; selected"), (Feature(2, "Auth", 2, False),))

    result = feature_store.apply_revision(revision, 10)

    assert result.success is False
    assert "cannot be removed" in result.error
    assert len(feature_store.load()) == 2  # unchanged


def test_apply_revision_rejeita_modificacao_de_feature_passada():
    feature_store.write([Feature(1, "API", 1, True)])
    revision = PlanRevision(
        "tweak passed feature", ("keep", "tweak; selected"), (Feature(1, "API v2", 1, False),)
    )

    result = feature_store.apply_revision(revision, 10)

    assert result.success is False
    assert "cannot be modified" in result.error


def test_apply_revision_aceita_reprioritizacao_de_pendente_preserva_passadas():
    feature_store.write([Feature(1, "API", 1, True), Feature(2, "Auth", 2, False)])
    revision = PlanRevision(
        "reprioritize",
        ("keep order", "swap; selected"),
        (Feature(1, "API", 1, False), Feature(2, "Auth", 1, False)),
    )

    result = feature_store.apply_revision(revision, 10)

    assert result.success is True
    loaded = {f.id: f for f in feature_store.load()}
    assert loaded[1].passes is True  # carried forward, immune to the proposal's passes=False
    assert loaded[2].priority == 1


def test_apply_revision_rejeita_grafo_de_dependencia_invalido():
    feature_store.write([])
    revision = PlanRevision(
        "cyclic", ("A", "B"), (Feature(1, "A", 1, False, (2,)), Feature(2, "B", 2, False, (1,)))
    )

    result = feature_store.apply_revision(revision, 10)

    assert result.success is False


def test_apply_revision_rejeita_menos_de_duas_alternativas():
    feature_store.write([])
    revision = PlanRevision("only one path considered", ("just this",), (Feature(1, "A", 1, False),))

    result = feature_store.apply_revision(revision, 10)

    assert result.success is False
    assert "alternatives" in result.error


# --- plan_observation_store ------------------------------------------------------------


def test_observation_ids_sao_sequenciais_com_tres_digitos():
    first = plan_observation_store.append("verification_failure", 1, "first")
    second = plan_observation_store.append("verification_failure", 1, "second")

    assert first.id == "OBS-001"
    assert second.id == "OBS-002"
    assert [o.id for o in plan_observation_store.load()] == ["OBS-001", "OBS-002"]


def test_observation_reset_apaga_o_log():
    plan_observation_store.append("verification_failure", 1, "first")

    plan_observation_store.reset()

    assert plan_observation_store.load() == []


# --- plan_revision_store ---------------------------------------------------------------


def test_fingerprint_ignora_o_estado_de_passes():
    a = [Feature(1, "A", 1, False), Feature(2, "B", 2, True)]
    b = [Feature(1, "A", 1, True), Feature(2, "B", 2, False)]  # same definitions, different Passes

    assert plan_revision_store.fingerprint(a) == plan_revision_store.fingerprint(b)


def test_fingerprint_muda_quando_o_plano_muda():
    a = [Feature(1, "A", 1, False)]
    b = [Feature(1, "A", 2, False)]  # different priority

    assert plan_revision_store.fingerprint(a) != plan_revision_store.fingerprint(b)


def test_reset_limpa_proposta_historico_e_ponteiro_atual(observation):
    Path(plan_revision_store.PROPOSAL_PATH).parent.mkdir(parents=True, exist_ok=True)
    Path(plan_revision_store.PROPOSAL_PATH).write_text("{}")
    revision = _revision(observation.id, Feature(1, "A", 1, False), Feature(2, "B", 2, False))
    evaluation = plan_revision_evaluator.evaluate([], revision, plan_observation_store.load(), 10, 80, 8)
    plan_revision_store.record(revision, list(revision.features), evaluation)
    assert plan_revision_store.revision_count() == 1

    plan_revision_store.reset()

    assert plan_revision_store.revision_count() == 0
    assert not Path(plan_revision_store.PROPOSAL_PATH).exists()
    assert not Path(".harness/plans").exists()


def test_feature_store_reset_tambem_limpa_revisoes_e_observacoes(observation):
    revision = _revision(observation.id, Feature(1, "A", 1, False), Feature(2, "B", 2, False))
    evaluation = plan_revision_evaluator.evaluate([], revision, plan_observation_store.load(), 10, 80, 8)
    plan_revision_store.record(revision, list(revision.features), evaluation)
    assert plan_revision_store.revision_count() == 1
    assert plan_observation_store.load() != []

    feature_store.reset()

    assert plan_revision_store.revision_count() == 0
    assert plan_observation_store.load() == []
