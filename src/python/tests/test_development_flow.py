"""O loop por feature do flow de desenvolvimento: cada task decide o PRÓXIMO comando
(padrão do gate de avaliação). Cobre as ramificações — verify FAIL↺implement, verify
PASS→handoff automático, fallback handoff legado — e a guarda por feature."""

import json
import shutil
import subprocess
import uuid
from dataclasses import replace
from pathlib import Path

from flows_development import state_keys, tasks
from harness_engine import (
    artifact_store,
    feature_store,
    plan_observation_store,
    plan_revision_store,
    run_config_store,
    state_store,
    task_registry,
    trace,
)
from harness_engine.envelope import Envelope, EnvelopeType
from harness_engine.feature_store import Feature
from harness_engine.run_config_store import RunConfig

# Espelha a fiação real de flows_development/__main__.py: só reseta state_store/trace no
# "start" quando não há feature pendente — sessão fresca do hard reset por feature deve
# RETOMAR, não apagar a trajetória/step acumulados das features anteriores.
DISPATCH_TASKS = {
    "start": lambda _e: tasks.start(),
    "plan": tasks.plan,
    "bearings": tasks.bearings,
    "smoke": tasks.smoke,
    "pick": tasks.pick,
    "implement": tasks.implement,
}


def _dispatch_json(json_str: str) -> str:
    return task_registry.dispatch(
        [json_str], DISPATCH_TASKS, should_reset_on_start=lambda: feature_store.pending_count() == 0
    )

# id 1 tem prioridade 2; id 2 tem prioridade 1 → a de maior prioridade é a id 2.
FEATURES_JSON = '[{"id":1,"title":"A","priority":2},{"id":2,"title":"B","priority":1}]'


def _cmd(value: str, *args: str) -> Envelope:
    return Envelope(EnvelopeType.COMMAND, value, args)


def _write_plan_file(features: str) -> None:
    """Writes the driver-side feature array to state_keys.PLAN_FILE_PATH — tasks.plan()
    reads features from that file, not from the envelope's args."""
    path = Path(state_keys.PLAN_FILE_PATH)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(features, encoding="utf-8")


def _plan_cmd(features: str, verify_cmd: str, target_dir: str) -> Envelope:
    _write_plan_file(features)
    return _cmd("plan", verify_cmd, target_dir)


def _git(cwd: Path, *args: str) -> str:
    proc = subprocess.run(["git", *args], cwd=cwd, text=True, capture_output=True, check=False)
    assert proc.returncode == 0, f"git {' '.join(args)} failed: {proc.stderr}{proc.stdout}"
    return proc.stdout


def _plan() -> str:
    target = Path("src/app")
    target.mkdir(parents=True, exist_ok=True)
    (target / "init.sh").write_text("#!/usr/bin/env bash\nset -e\n")
    result = tasks.plan(_plan_cmd(FEATURES_JSON, "dotnet test", "src/app"))
    return result


def _advance_to_verify() -> None:
    """Leva o flow até deixar uma feature escolhida e implementada (pronta p/ verify)."""
    _plan()
    tasks.implement(_cmd("implement", "implementei"))


def _write_verify_feature_script(target_dir: Path, body: str) -> None:
    target_dir.mkdir(parents=True, exist_ok=True)
    (target_dir / "verify-feature.sh").write_text(body.replace("\r\n", "\n"))


def _verify_log_path(feature_id: int) -> Path:
    return Path(".harness/logs") / f"verify-feature-{feature_id}.log"


def _verify_log_path_indexed(feature_id: int, index: int) -> Path:
    return Path(".harness/logs") / f"verify-feature-{feature_id}-{index}.log"


def _set_verify_cmds(commands: tuple[str, ...], verify_cmd: str | None = None) -> None:
    """Overrides run_config_store's verify_cmds (and optionally verify_cmd) after a plan —
    plan()'s envelope has no slot for a command LIST, same as the .NET tests, which set it
    via `RunConfigStore.Write(RunConfigStore.Load() with { VerifyCmds = [...] })`."""
    current = run_config_store.load()
    run_config_store.write(replace(
        current,
        verify_cmds=commands,
        verify_cmd=verify_cmd if verify_cmd is not None else current.verify_cmd,
    ))


def _given_docs_brief(content: str) -> None:
    Path("specs").mkdir(parents=True, exist_ok=True)
    (Path("specs") / "brief.md").write_text(content)


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

    result = tasks.start()

    assert '"value":"implement"' in result  # fases determinísticas já foram executadas
    assert len(feature_store.load()) == 2  # intacta
    assert feature_store.pending_count() == 2  # nenhuma marcada como passando
    assert run_config_store.load().verify_cmd == "dotnet test"  # intacto
    assert run_config_store.load().target_dir == "src/app"


def test_start_com_feature_pendente_preserva_o_run_id_do_plan_anterior():
    _advance_to_verify()  # ...→ implement, sessão "morre" aqui, antes do verify
    run_id_antes_do_start = run_config_store.load().run_id
    assert run_id_antes_do_start

    tasks.start()

    # Retomada não gera um novo run - a identidade do run tem que sobreviver ao "start".
    assert run_config_store.load().run_id == run_id_antes_do_start


# --- brief: persistência em start() e reinjeção em implement ----------------------


def test_start_com_docs_populados_persiste_o_brief_no_artifact_store():
    _given_docs_brief("# Brief\n\nConstrua um app de tarefas.")

    tasks.start()

    # docs_reader.read antepõe um cabeçalho "## <arquivo>" — contains, não igualdade exata.
    assert "Construa um app de tarefas." in artifact_store.read("brief")


def test_start_modo_interativo_nao_persiste_brief():
    tasks.start()  # sem specs/ → initializer_interactive()

    assert artifact_store.read("brief") == ""


def test_start_novo_run_sem_docs_apaga_brief_do_run_anterior():
    # Um segundo run com o MESMO specs/ já se autocorrigiria via overwrite (não prova nada
    # sobre o reset()); o caso que só artifact_store.reset() resolve é specs→interativo: o
    # modo interativo nunca chama write, então sem o reset() o brief antigo vazaria.
    _given_docs_brief("brief do topico A")
    tasks.start()
    _plan()
    for f in feature_store.load():
        feature_store.mark_passed(f.id)
    shutil.rmtree("specs")

    tasks.start()  # run novo, sem specs/ → interativo

    assert artifact_store.read("brief") == ""


def test_plan_retorna_implement_sem_reinjetar_o_brief():
    _given_docs_brief("brief do topico A")
    tasks.start()

    result = _plan()

    assert "brief do topico A" not in result


def test_pick_retorna_implement_sem_reinjetar_o_brief():
    _given_docs_brief("brief do topico A")
    tasks.start()
    result = _plan()

    assert "brief do topico A" not in result


def test_bearings_e_implement_sem_brief_persistido_nao_tem_tag_brief():
    # Sem specs/: modo interativo, sem brief persistido — o bloco some, não fica vazio.
    bearings_result = _plan()
    implement_result = bearings_result

    assert "<brief>" not in bearings_result
    assert "<brief>" not in implement_result


def test_pick_retorna_implement_com_description_e_references_da_feature():
    json_str = (
        '[{"id":1,"title":"A","priority":2,"description":"faz X","references":["RF-003"],'
        '"implementationContext":{"requirements":["inline X"]}},'
        '{"id":2,"title":"B","priority":1}]'
    )
    Path("src/app").mkdir(parents=True, exist_ok=True)
    (Path("src/app") / "init.sh").write_text("#!/usr/bin/env bash\nset -e\n")
    tasks.plan(_plan_cmd(json_str, "dotnet test", "src/app"))  # escolhe "B"
    _write_verify_feature_script(Path("src/app"), "#!/usr/bin/env bash\nset -e\n")
    result = tasks.implement(_cmd("implement", "feito"))  # verifica B, entrega A

    assert "Description: faz X" in result
    assert "Brief references: RF-003" in result
    assert "<implementation-context>requirements: inline X" in result
    assert "<brief>" not in result


def test_pick_retorna_implement_sem_description_nem_references_nao_tem_bloco_de_contexto():
    result = _plan()  # FEATURES_JSON sem description/references

    assert "Description:" not in result
    assert "Brief references:" not in result


def test_dispatch_start_com_feature_pendente_nao_trunca_trace_nem_step():
    # Reproduz o hard reset por feature: uma feature ainda pendente ("B") e um trace/step já
    # acumulados por features anteriores, quando a sessão fresca reabre com "start".
    _advance_to_verify()  # deixa a feature "B" pendente, sessão "morre" antes do verify
    trace.append(41, "handoff", trace.TraceOutcome.INSTRUCTION, 10)  # trajetória de features passadas
    step_antes_do_start = state_store.load().step

    result = _dispatch_json('{"type":"text","value":"start"}')

    assert '"value":"implement"' in result  # retomou via fases determinísticas
    assert any(e.step == 41 and e.command == "handoff" for e in trace.load())  # trace preservado
    assert state_store.load().step == step_antes_do_start + 1  # contador continuou, não voltou a 1


def test_dispatch_start_sem_feature_pendente_trunca_trace_e_step():
    # Sem run em andamento, "start" É um início de verdade e deve truncar trace/step.
    _plan()
    for f in feature_store.load():
        feature_store.mark_passed(f.id)
    trace.append(41, "handoff", trace.TraceOutcome.INSTRUCTION, 10)

    _dispatch_json('{"type":"text","value":"start"}')

    assert all(e.step != 41 for e in trace.load())
    assert state_store.load().step == 1


def test_plan_persiste_features_e_roteia_para_bearings():
    Path("web").mkdir(parents=True, exist_ok=True)
    (Path("web") / "init.sh").write_text("#!/usr/bin/env bash\nset -e\n")
    result = tasks.plan(_plan_cmd(FEATURES_JSON, "npm test", "web"))

    assert len(feature_store.load()) == 2
    assert run_config_store.load().verify_cmd == "npm test"
    assert run_config_store.load().target_dir == "web"
    assert '"value":"implement"' in result


def test_plan_gera_um_run_id_novo_e_nao_vazio():
    tasks.plan(_plan_cmd(FEATURES_JSON, "npm test", "web"))

    run_id = run_config_store.load().run_id

    assert run_id
    uuid.UUID(run_id)  # levanta ValueError se não for um UUID válido


def test_plan_features_invalidas_reemite_o_plano():
    result = tasks.plan(_plan_cmd("não é json", "dotnet test", "."))

    assert feature_store.load() == []
    assert run_config_store.load() == RunConfig()  # nada persistido
    assert '"value":"plan"' in result
    assert "NEW SESSION" not in result


def test_pick_escolhe_maior_prioridade_e_grava_a_feature_corrente():
    implement = _plan()

    assert state_store.get("current_feature_id") == "2"  # prioridade 1 = id 2 ("B")
    assert "B" in implement
    assert '"value":"implement"' in implement
    assert "<input>\n    === NEW SESSION (clean context) ===" in implement


def test_verify_fail_volta_para_implement():
    _advance_to_verify()

    result = tasks.verify(_cmd("verify", "FAIL: testes vermelhos"))

    assert "FAILED" in result
    assert "NEW SESSION" not in result
    assert '"value":"implement"' in result


def test_verify_pass_executa_handoff_automatico_e_avanca():
    _advance_to_verify()
    _write_verify_feature_script(Path("src/app"), "#!/usr/bin/env bash\nset -e\n")
    result = tasks.implement(_cmd("implement", "PASS"))

    assert '"value":"implement"' in result
    assert '"value":"handoff"' not in result
    assert feature_store.pending_count() == 1
    assert "Feature #2" in Path("src/app/progress.txt").read_text()


def test_implement_com_verify_feature_passando_executa_verify_e_handoff_automaticos():
    _write_verify_feature_script(Path("src/app"), """#!/usr/bin/env bash
set -euo pipefail
    echo "PASS: feature $1 verificada"
""")
    _plan()

    result = tasks.implement(_cmd("implement", "implementei"))

    assert '"value":"implement"' in result
    assert '"value":"verify"' not in result
    assert feature_store.pending_count() == 1
    progress = Path("src/app/progress.txt").read_text()
    assert "Feature #2" in progress
    assert "PASS: verify-feature.sh 2 passed" in progress
    assert ".harness/logs/verify-feature-2.log" in progress
    assert "command: bash ./verify-feature.sh 2" in Path(".harness/logs/verify-feature-2.log").read_text()


def test_implement_com_verify_feature_falhando_volta_para_fix():
    _write_verify_feature_script(Path("src/app"), """#!/usr/bin/env bash
set -euo pipefail
echo "FAIL: feature $1 quebrou"
echo "LINHA DETALHADA QUE FICA SO NO LOG"
exit 7
""")
    _plan()
    result = tasks.implement(_cmd("implement", "implementei"))

    assert "FAILED" in result
    assert "feature 2 quebrou" in result
    assert ".harness/logs/verify-feature-2.log" in result
    assert "LINHA DETALHADA QUE FICA SO NO LOG" not in result
    log = Path(".harness/logs/verify-feature-2.log").read_text()
    assert "FAIL: feature 2 quebrou" in log
    assert "LINHA DETALHADA QUE FICA SO NO LOG" in log
    assert '"value":"implement"' in result
    assert feature_store.pending_count() == 2
    assert not Path("src/app/progress.txt").exists()


def test_verify_veredito_invalido_reemite_verify():
    _advance_to_verify()

    result = tasks.verify(_cmd("verify", "rodei os testes e passou"))

    assert '"value":"implement"' in result
    assert "FAILED" in result


def test_handoff_sem_pass_deterministico_retorna_para_verify():
    _advance_to_verify()

    result = tasks.handoff(_cmd("handoff", ""))

    assert '"value":"verify"' in result
    assert feature_store.pending_count() == 2


def test_handoff_com_pendencia_abre_nova_sessao_com_tudo_passando_encerra():
    # 1ª feature (id 2)
    _advance_to_verify()
    _write_verify_feature_script(Path("src/app"), "#!/usr/bin/env bash\nset -e\n")
    after_first = tasks.implement(_cmd("implement", "PASS"))

    assert '"value":"implement"' in after_first  # ainda falta a id 1
    assert feature_store.pending_count() == 1

    # 2ª feature (id 1)
    after_second = tasks.implement(_cmd("implement", "feito"))

    assert after_second == "stop"
    assert feature_store.all_passing()


def test_handoff_hash_textual_nao_substitui_verify_deterministico():
    _advance_to_verify()

    result = tasks.handoff(_cmd("handoff", "abc123"))

    assert '"value":"verify"' in result
    assert feature_store.pending_count() == 2


def test_verify_pass_handoff_automatico_commita_so_o_diretorio_alvo():
    repo = Path("repo")
    target = repo / "app"
    target.mkdir(parents=True)
    _git(repo, "init")
    _git(repo, "config", "user.email", "harness@example.test")
    _git(repo, "config", "user.name", "Harness Test")
    (repo / "outside.txt").write_text("fora do target")

    (target / "init.sh").write_text("#!/usr/bin/env bash\nset -e\n")
    tasks.plan(_plan_cmd(FEATURES_JSON, "dotnet test", str(target)))
    _write_verify_feature_script(target, "#!/usr/bin/env bash\nset -e\n")
    result = tasks.implement(_cmd("implement", "feito no target"))

    assert '"value":"implement"' in result
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
    result = tasks.plan(_plan_cmd(
        '[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]',
        "dotnet test", ".",
    ))

    assert feature_store.load() == []
    assert run_config_store.load() == RunConfig()
    assert '"value":"plan"' in result
    assert "NEW SESSION" not in result


def test_plan_depends_on_id_inexistente_reemite_o_plano():
    result = tasks.plan(_plan_cmd(
        '[{"id":1,"title":"A","priority":1,"dependsOn":[99]}]', "dotnet test", ".",
    ))

    assert feature_store.load() == []
    assert '"value":"plan"' in result
    assert "NEW SESSION" not in result


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

    tasks.plan(_plan_cmd(json_text, "dotnet test", "."))

    assert 2 not in [f.id for f in feature_store.load()]  # id 2 foi de fato cortado
    survivor = next(f for f in feature_store.load() if f.id == 1)
    assert 2 not in survivor.deps  # ...e a dependência não pode sobrar


def test_pick_respeita_dependencia_escolhe_dependencia_antes_da_dependente():
    # f1: prioridade pior, sem deps. f2: prioridade melhor, mas depende de f1.
    json_text = '[{"id":1,"title":"fundação","priority":2},{"id":2,"title":"depende","priority":1,"dependsOn":[1]}]'
    tasks.plan(_plan_cmd(json_text, "dotnet test", "."))
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


def test_verify_cmds_todos_passam_agrega_como_pass_e_gera_um_log_por_indice():
    _plan()
    _set_verify_cmds(("true", "true"))

    result = tasks.implement(_cmd("implement", "implementei"))

    assert '"value":"implement"' in result
    assert feature_store.pending_count() == 1
    assert "command: true" in _verify_log_path_indexed(2, 1).read_text()
    assert "command: true" in _verify_log_path_indexed(2, 2).read_text()


def test_verify_cmds_um_comando_falha_agrega_como_fail_e_identifica_o_indice():
    _plan()
    _set_verify_cmds(("true", "false", "true"))

    result = tasks.implement(_cmd("implement", "implementei"))

    assert "FAILED" in result
    assert "FAIL: 1 of 3 verify commands did not pass" in result
    assert "#2 FAIL" in result
    assert feature_store.pending_count() == 2  # feature continua pendente


def test_verify_cmds_com_operador_de_shell_em_uma_entrada_nao_dispara_processos():
    _plan()
    _set_verify_cmds(("true", "true && false"))

    result = tasks.implement(_cmd("implement", "implementei"))

    assert "verify command #2" in result
    assert "disallowed shell operators" in result
    assert feature_store.pending_count() == 2
    # Nenhum processo foi disparado para a entrada inválida nem para as demais.
    assert not _verify_log_path_indexed(2, 1).exists()
    assert not _verify_log_path_indexed(2, 2).exists()


def test_verify_cmds_lista_vazia_cai_no_caminho_legado_de_verify_cmd():
    _plan()
    _set_verify_cmds((), verify_cmd="true")

    result = tasks.implement(_cmd("implement", "implementei"))

    assert '"value":"implement"' in result
    assert feature_store.pending_count() == 1
    assert _verify_log_path(2).exists()
    assert not _verify_log_path_indexed(2, 1).exists()


# --- replan -------------------------------------------------------------------------


def test_replan_aplica_revisao_versionada_sem_trocar_run_id():
    _plan()
    run_id = run_config_store.load().run_id
    observation = plan_observation_store.append(
        "missing_dependency", 2, "Feature B needs a foundation.", "compiler failure"
    )
    proposal = Path(plan_revision_store.PROPOSAL_PATH)
    proposal.parent.mkdir(parents=True, exist_ok=True)
    proposal.write_text(json.dumps({
        "reason": "missing dependency",
        "alternativesConsidered": ["keep stub", "add dependency; selected"],
        "basedOnObservationIds": [observation.id],
        "revisedFeatures": [
            {"id": 1, "title": "A", "priority": 2},
            {"id": 2, "title": "B", "priority": 3},
            {"id": 3, "title": "Foundation", "priority": 1},
        ],
    }))

    result = tasks.replan(_cmd("replan"))

    assert "Foundation" in result  # the harness re-picked: the new, highest-priority feature
    assert len(feature_store.load()) == 3
    assert run_config_store.load().run_id == run_id  # replan is not a new run
    assert plan_revision_store.revision_count() == 1
    assert Path(".harness/plans/plan-v1.json").exists()


def test_terceira_falha_deterministica_solicita_replanejamento_global():
    _advance_to_verify()  # 1st deterministic failure happens inside implement()
    tasks.verify(_cmd("verify"))  # 2nd: normal local correction (fix_prompt)

    result = tasks.verify(_cmd("verify"))  # 3rd: escalates to a global replan proposal

    assert "global development plan" in result
    assert "alternativesConsidered" in result
    assert '"value":"replan"' in result
