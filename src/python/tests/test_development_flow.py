"""O loop por feature do flow de desenvolvimento: cada task decide o PRÓXIMO comando
(padrão do gate de avaliação). Cobre as ramificações — verify FAIL↺implement, verify
PASS→handoff automático, fallback handoff legado — e a guarda por feature."""

import subprocess
from pathlib import Path

from flows_development import tasks
from harness_engine import feature_store, run_config_store, state_store
from harness_engine.envelope import Envelope, EnvelopeType
from harness_engine.feature_store import Feature
from harness_engine.run_config_store import RunConfig

# id 1 tem prioridade 2; id 2 tem prioridade 1 → a de maior prioridade é a id 2.
FEATURES_JSON = '[{"id":1,"title":"A","priority":2},{"id":2,"title":"B","priority":1}]'


def _cmd(value: str, *args: str) -> Envelope:
    return Envelope(EnvelopeType.COMMAND, value, args)


def _git(cwd: Path, *args: str) -> str:
    proc = subprocess.run(["git", *args], cwd=cwd, text=True, capture_output=True, check=False)
    assert proc.returncode == 0, f"git {' '.join(args)} failed: {proc.stderr}{proc.stdout}"
    return proc.stdout


def _plan() -> str:
    return tasks.plan(_cmd("plan", FEATURES_JSON, "dotnet test", "src/app"))


def _advance_to_verify() -> None:
    """Leva o flow até deixar uma feature escolhida e implementada (pronta p/ verify)."""
    _plan()
    tasks.bearings(_cmd("bearings", "orientado"))
    tasks.smoke(_cmd("smoke", "baseline ok"))
    tasks.pick(_cmd("pick"))
    tasks.implement(_cmd("implement", "implementei"))


def _write_verify_feature_script(target_dir: Path, body: str) -> None:
    target_dir.mkdir(parents=True, exist_ok=True)
    (target_dir / "verify-feature.sh").write_text(body.replace("\r\n", "\n"))


def _verify_log_path(feature_id: int) -> Path:
    return Path(".harness/logs") / f"verify-feature-{feature_id}.log"


def test_start_sem_feature_pendente_reseta_feature_list_e_run_config():
    # Um run anterior terminou (tudo passando) - "start" pode começar de verdade do zero.
    _plan()
    for f in feature_store.load():
        feature_store.mark_passed(f.id)
    assert feature_store.load() != []

    tasks.start()

    assert feature_store.load() == []
    assert run_config_store.load() == RunConfig()


def test_start_com_feature_pendente_retoma_via_bearings_em_vez_de_resetar():
    # Uma sessão anterior (talvez outro driver) morreu no meio da feature "B" (id 2, ainda
    # pendente). "start" não pode apagar nada - deve rotear direto para bearings.
    _advance_to_verify()  # ...→ implement, sessão "morre" aqui, antes do verify

    # task_registry.dispatch sempre reseta state.json incondicionalmente antes de chamar
    # o start() do domínio - reproduz isso aqui, já que este teste chama start() diretamente.
    state_store.reset()

    result = tasks.start()

    assert "NOVA SESSÃO" in result  # bearings_prompt, não o inicializador
    assert len(feature_store.load()) == 2  # intacta
    assert feature_store.pending_count() == 2  # nenhuma marcada como passando
    assert run_config_store.load().verify_cmd == "dotnet test"  # intacto
    assert run_config_store.load().target_dir == "src/app"


def test_plan_persiste_features_e_roteia_para_bearings():
    result = tasks.plan(_cmd("plan", FEATURES_JSON, "npm test", "web"))

    assert len(feature_store.load()) == 2
    assert run_config_store.load().verify_cmd == "npm test"
    assert run_config_store.load().target_dir == "web"
    assert "NOVA SESSÃO" in result
    assert '"value":"bearings"' in result


def test_plan_features_invalidas_reemite_o_plano():
    result = tasks.plan(_cmd("plan", "não é json", "dotnet test", "."))

    assert feature_store.load() == []
    assert run_config_store.load() == RunConfig()  # nada persistido
    assert '"value":"plan"' in result
    assert "NOVA SESSÃO" not in result


def test_pick_escolhe_maior_prioridade_e_grava_a_feature_corrente():
    _plan()
    tasks.bearings(_cmd("bearings", "ok"))
    after_smoke = tasks.smoke(_cmd("smoke", "ok"))
    assert '"value":"pick"' in after_smoke

    implement = tasks.pick(_cmd("pick"))

    assert state_store.get("current_feature_id") == "2"  # prioridade 1 = id 2 ("B")
    assert "B" in implement
    assert '"value":"implement"' in implement


def test_verify_fail_volta_para_implement():
    _advance_to_verify()

    result = tasks.verify(_cmd("verify", "FAIL: testes vermelhos"))

    assert "FALHOU" in result
    assert '"value":"implement"' in result


def test_verify_pass_executa_handoff_automatico_e_avanca():
    _advance_to_verify()

    result = tasks.verify(_cmd("verify", "PASS"))

    assert "NOVA SESSÃO" in result
    assert '"value":"handoff"' not in result
    assert feature_store.pending_count() == 1
    assert "Feature #2" in Path("src/app/progress.txt").read_text()


def test_implement_com_verify_feature_passando_executa_verify_e_handoff_automaticos():
    _write_verify_feature_script(Path("src/app"), """#!/usr/bin/env bash
set -euo pipefail
echo "PASS: feature $1 verificada"
""")
    _plan()
    tasks.bearings(_cmd("bearings", "orientado"))
    tasks.smoke(_cmd("smoke", "baseline ok"))
    tasks.pick(_cmd("pick"))

    result = tasks.implement(_cmd("implement", "implementei"))

    assert "NOVA SESSÃO" in result
    assert '"value":"verify"' not in result
    assert feature_store.pending_count() == 1
    progress = Path("src/app/progress.txt").read_text()
    assert "Feature #2" in progress
    assert "PASS: feature 2 verificada" in progress
    assert ".harness/logs/verify-feature-2.log" in progress
    assert "command: bash ./verify-feature.sh 2" in _verify_log_path(2).read_text()


def test_implement_com_verify_feature_falhando_volta_para_fix():
    _write_verify_feature_script(Path("src/app"), """#!/usr/bin/env bash
set -euo pipefail
echo "FAIL: feature $1 quebrou"
echo "LINHA DETALHADA QUE FICA SO NO LOG"
exit 7
""")
    _plan()
    tasks.bearings(_cmd("bearings", "orientado"))
    tasks.smoke(_cmd("smoke", "baseline ok"))
    tasks.pick(_cmd("pick"))

    result = tasks.implement(_cmd("implement", "implementei"))

    assert "FALHOU" in result
    assert "feature 2 quebrou" in result
    assert ".harness/logs/verify-feature-2.log" in result
    assert "LINHA DETALHADA QUE FICA SO NO LOG" not in result
    log = _verify_log_path(2).read_text()
    assert "FAIL: feature 2 quebrou" in log
    assert "LINHA DETALHADA QUE FICA SO NO LOG" in log
    assert '"value":"implement"' in result
    assert feature_store.pending_count() == 2
    assert not Path("src/app/progress.txt").exists()


def test_verify_veredito_invalido_reemite_verify():
    _advance_to_verify()

    result = tasks.verify(_cmd("verify", "rodei os testes e passou"))

    assert '"value":"verify"' in result
    assert '"value":"handoff"' not in result
    assert "não começou" in result


def test_handoff_vazio_reemite_handoff_e_nao_marca_feature_como_passando():
    _advance_to_verify()

    result = tasks.handoff(_cmd("handoff", ""))

    assert '"value":"handoff"' in result
    assert feature_store.pending_count() == 2


def test_handoff_com_pendencia_abre_nova_sessao_com_tudo_passando_encerra():
    # 1ª feature (id 2)
    _advance_to_verify()
    after_first = tasks.verify(_cmd("verify", "PASS"))

    assert "NOVA SESSÃO" in after_first  # ainda falta a id 1
    assert feature_store.pending_count() == 1

    # 2ª feature (id 1)
    tasks.bearings(_cmd("bearings", "ok"))
    tasks.smoke(_cmd("smoke", "ok"))
    tasks.pick(_cmd("pick"))
    tasks.implement(_cmd("implement", "feito"))
    after_second = tasks.verify(_cmd("verify", "PASS"))

    assert after_second == "stop"
    assert feature_store.all_passing()


def test_handoff_legado_com_hash_marca_feature_como_passando():
    _advance_to_verify()

    result = tasks.handoff(_cmd("handoff", "abc123"))

    assert "NOVA SESSÃO" in result
    assert feature_store.pending_count() == 1


def test_verify_pass_handoff_automatico_commita_so_o_diretorio_alvo():
    repo = Path("repo")
    target = repo / "app"
    target.mkdir(parents=True)
    _git(repo, "init")
    _git(repo, "config", "user.email", "harness@example.test")
    _git(repo, "config", "user.name", "Harness Test")
    (repo / "outside.txt").write_text("fora do target")

    tasks.plan(_cmd("plan", FEATURES_JSON, "dotnet test", str(target)))
    tasks.bearings(_cmd("bearings", "ok"))
    tasks.smoke(_cmd("smoke", "ok"))
    tasks.pick(_cmd("pick"))
    tasks.implement(_cmd("implement", "feito no target"))

    result = tasks.verify(_cmd("verify", "PASS: tudo verde"))

    assert "NOVA SESSÃO" in result
    committed_files = _git(repo, "show", "--name-only", "--format=", "HEAD")
    assert "app/progress.txt" in committed_files
    assert "outside.txt" not in committed_files
    assert "?? outside.txt" in _git(repo, "status", "--short")


def test_guarda_por_feature_ao_exceder_o_teto_encerra():
    _plan()
    tasks.bearings(_cmd("bearings", "ok"))  # zera para 1
    state_store.set("feature_steps", str(tasks.STEPS_PER_FEATURE))  # no limite

    result = tasks.smoke(_cmd("smoke", "ok"))  # próximo bump ultrapassa

    assert result == "stop"


def test_plan_depends_on_ciclico_reemite_o_plano():
    result = tasks.plan(_cmd(
        "plan",
        '[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]',
        "dotnet test", ".",
    ))

    assert feature_store.load() == []
    assert run_config_store.load() == RunConfig()
    assert '"value":"plan"' in result
    assert "NOVA SESSÃO" not in result


def test_plan_depends_on_id_inexistente_reemite_o_plano():
    result = tasks.plan(_cmd(
        "plan", '[{"id":1,"title":"A","priority":1,"dependsOn":[99]}]', "dotnet test", ".",
    ))

    assert feature_store.load() == []
    assert '"value":"plan"' in result
    assert "NOVA SESSÃO" not in result


def test_plan_corte_max_features_remove_dependencia_para_id_cortado():
    # id 1 (prioridade 1, a melhor) sobrevive ao corte; depende do id 2, cuja prioridade
    # (1000) é a pior de todas — garantidamente cortado pelo corte em MAX_FEATURES. Os
    # "extras" preenchem as vagas restantes com prioridades intermediárias.
    extras = ",".join(
        f'{{"id":{i},"title":"extra{i}","priority":{i}}}'
        for i in range(3, 3 + tasks.MAX_FEATURES - 1)
    )
    json_text = (
        '[{"id":1,"title":"sobrevivente","priority":1,"dependsOn":[2]},'
        '{"id":2,"title":"cortada","priority":1000},' + extras + "]"
    )

    tasks.plan(_cmd("plan", json_text, "dotnet test", "."))

    assert 2 not in [f.id for f in feature_store.load()]  # id 2 foi de fato cortado
    survivor = next(f for f in feature_store.load() if f.id == 1)
    assert 2 not in survivor.deps  # ...e a dependência não pode sobrar


def test_pick_respeita_dependencia_escolhe_dependencia_antes_da_dependente():
    # f1: prioridade pior, sem deps. f2: prioridade melhor, mas depende de f1.
    json_text = '[{"id":1,"title":"fundação","priority":2},{"id":2,"title":"depende","priority":1,"dependsOn":[1]}]'
    tasks.plan(_cmd("plan", json_text, "dotnet test", "."))
    tasks.bearings(_cmd("bearings", "ok"))
    tasks.smoke(_cmd("smoke", "ok"))

    tasks.pick(_cmd("pick"))

    assert state_store.get("current_feature_id") == "1"


def test_pick_sem_feature_pronta_mas_com_pendencia_encerra_sem_reportar_concluido():
    # Grafo bloqueado gravado direto via write (bypassando a validação de parse).
    _plan()  # popula run_config; a lista será sobrescrita a seguir
    feature_store.write([
        Feature(1, "A", 1, False, (2,)),
        Feature(2, "B", 2, False, (1,)),
    ])
    tasks.bearings(_cmd("bearings", "ok"))
    tasks.smoke(_cmd("smoke", "ok"))

    result = tasks.pick(_cmd("pick"))

    assert result == "stop"
    assert feature_store.pending_count() == 2  # nada foi marcado como passando
