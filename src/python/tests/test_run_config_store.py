"""verify_cmd/target_dir live outside state.json on purpose: they need to survive the
unconditional reset task_registry.dispatch does to state.json on every "start", so a
resumed run (pending feature) still works in smoke/verify without needing a new
"plan"."""

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
