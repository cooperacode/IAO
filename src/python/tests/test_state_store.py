"""O store é o que permite manter o envelope mínimo (economia de tokens): o estado
acumulado fica em arquivo entre invocações, não na janela de contexto."""

from harness_engine import state_store


def test_set_e_get_persistem_entre_chamadas():
    state_store.set("descricao", "Login com Google")

    assert state_store.get("descricao") == "Login com Google"


def test_get_chave_inexistente_retorna_none():
    assert state_store.get("nao-existe") is None


def test_set_sobrescreve_a_chave_existente():
    state_store.set("tipo", "Bug")
    state_store.set("tipo", "Épico")

    assert state_store.get("tipo") == "Épico"


def test_increment_avanca_o_contador():
    assert state_store.increment() == 1
    assert state_store.increment() == 2
    assert state_store.increment() == 3
    assert state_store.load().step == 3


def test_increment_preserva_os_dados_acumulados():
    state_store.set("descricao", "x")
    state_store.increment()

    assert state_store.get("descricao") == "x"


def test_reset_limpa_contador_e_dados():
    state_store.set("descricao", "x")
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
