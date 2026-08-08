"""verify_cmd/target_dir live outside state.json on purpose: they need to survive the
unconditional reset task_registry.dispatch does to state.json on every "start", so a
resumed run (pending feature) still works in smoke/verify without needing a new
"plan"."""

from pathlib import Path

from harness_engine import run_config_store
from harness_engine.run_config_store import RunConfig


def test_write_e_load_fazem_roundtrip():
    run_config_store.write(RunConfig("npm test", "app"))

    loaded = run_config_store.load()

    assert loaded.verify_cmd == "npm test"
    assert loaded.target_dir == "app"


def test_write_e_load_preservam_o_run_id():
    run_config_store.write(RunConfig("npm test", "app", "019b1ed0-6bea-7bc1-a790-0bdb42bb8ab6"))

    loaded = run_config_store.load()

    assert loaded.run_id == "019b1ed0-6bea-7bc1-a790-0bdb42bb8ab6"


def test_load_arquivo_ausente_retorna_defaults_sem_lancar():
    loaded = run_config_store.load()

    assert loaded.verify_cmd == ""
    assert loaded.target_dir == "."


def test_reset_apaga_o_arquivo():
    run_config_store.write(RunConfig("npm test", "app"))

    run_config_store.reset()

    assert run_config_store.load() == RunConfig()


def test_reset_sem_arquivo_nao_lanca():
    run_config_store.reset()  # no-op, must not throw


def test_write_e_load_fazem_roundtrip_com_verify_cmds():
    run_config_store.write(RunConfig("npm test", "app", verify_cmds=("npm run lint", "npm run typecheck")))

    loaded = run_config_store.load()

    assert loaded.verify_cmds is not None
    assert loaded.verify_cmds == ("npm run lint", "npm run typecheck")


def test_load_run_config_legado_sem_verify_cmds_carrega_com_none():
    # Simulates a run_config.json written by an earlier harness version, without the
    # "verifyCmds" key — proves the single-command path stays the default.
    Path(".harness").mkdir(exist_ok=True)
    Path(".harness/run_config.json").write_text(
        '{"verifyCmd":"npm test","targetDir":"app","runId":""}'
    )

    loaded = run_config_store.load()

    assert loaded.verify_cmd == "npm test"
    assert loaded.verify_cmds is None
