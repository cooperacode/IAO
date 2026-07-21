"""verify_cmd/target_dir vivem fora de state.json de propósito: precisam sobreviver ao
reset incondicional que task_registry.dispatch faz em state.json a cada "start", para que
um run retomado (feature pendente) ainda funcione em smoke/verify sem precisar de um novo
"plan"."""

from harness_engine import run_config_store
from harness_engine.run_config_store import RunConfig


def test_write_e_load_fazem_roundtrip():
    run_config_store.write(RunConfig("npm test", "app"))

    loaded = run_config_store.load()

    assert loaded.verify_cmd == "npm test"
    assert loaded.target_dir == "app"


def test_load_arquivo_ausente_retorna_defaults_sem_lancar():
    loaded = run_config_store.load()

    assert loaded.verify_cmd == ""
    assert loaded.target_dir == "."


def test_reset_apaga_o_arquivo():
    run_config_store.write(RunConfig("npm test", "app"))

    run_config_store.reset()

    assert run_config_store.load() == RunConfig()


def test_reset_sem_arquivo_nao_lanca():
    run_config_store.reset()  # no-op, não deve lançar
