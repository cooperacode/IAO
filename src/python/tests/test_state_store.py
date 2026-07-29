"""The store is what keeps the envelope minimal (token savings): the accumulated state
lives in a file between invocations, not in the context window."""

from pathlib import Path

from harness_engine import state_store


def test_set_e_get_persistem_entre_chamadas():
    state_store.set("description", "Login with Google")

    assert state_store.get("description") == "Login with Google"


def test_get_chave_inexistente_retorna_none():
    assert state_store.get("does-not-exist") is None


def test_set_sobrescreve_a_chave_existente():
    state_store.set("type", "Bug")
    state_store.set("type", "Epic")

    assert state_store.get("type") == "Epic"


def test_increment_avanca_o_contador():
    assert state_store.increment() == 1
    assert state_store.increment() == 2
    assert state_store.increment() == 3
    assert state_store.load().step == 3


def test_increment_preserva_os_dados_acumulados():
    state_store.set("description", "x")
    state_store.increment()

    assert state_store.get("description") == "x"


def test_reset_limpa_contador_e_dados():
    state_store.set("description", "x")
    state_store.increment()

    state_store.reset()

    assert state_store.load().step == 0
    assert state_store.get("descricao") is None


def test_set_context_e_get_context_persistem_entre_chamadas():
    state_store.set_context({"driver": "claude code"})

    assert state_store.get_context()["driver"] == "claude code"


def test_get_context_sem_contexto_definido_retorna_none():
    assert state_store.get_context() is None


def test_reset_limpa_o_contexto():
    state_store.set_context({"driver": "claude code"})

    state_store.reset()

    assert state_store.get_context() is None


def test_save_nao_deixa_arquivo_temporario_para_tras():
    # Atomic write: temp in the same directory + os.replace. After saving, only the
    # final file should remain in .harness — no leftover "state.json.tmp-*".
    state_store.set("description", "x")

    tmp_leftovers = list(Path(".harness").glob("state.json.tmp-*"))

    assert tmp_leftovers == []
    assert Path(".harness/state.json").exists()
