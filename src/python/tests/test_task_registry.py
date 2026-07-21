"""Regressões do endurecimento: erro NUNCA pode virar "stop" silencioso, e o teto de
passos precisa cortar loop infinito (guarda de token)."""

from harness_engine import prompt_formatter, state_store, task_registry
from harness_engine.envelope import Envelope, EnvelopeType

TASKS = {
    "start": lambda _e: "PROMPT_START",
    "classify": lambda e: f"PROMPT_CLASSIFY:{e.args[0] if e and e.args else ''}",
    "finalize": lambda _e: "stop",
}


def test_dispatch_comando_registrado_executa_a_action():
    result = task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    assert result == "PROMPT_START"


def test_dispatch_repassa_args_para_a_action():
    result = task_registry.dispatch(['{"type":"tool","value":"classify","args":["Login"]}'], TASKS)

    assert result == "PROMPT_CLASSIFY:Login"


def test_dispatch_finalize_retorna_stop():
    result = task_registry.dispatch(['{"type":"command","value":"finalize"}'], TASKS)

    assert result == "stop"


def test_dispatch_comando_inexistente_retorna_erro_e_nao_stop():
    result = task_registry.dispatch(['{"type":"text","value":"tipo"}'], TASKS)

    assert result.startswith("ERRO")
    assert result != "stop"
    assert "'tipo'" in result


def test_dispatch_json_malformado_retorna_erro_e_nao_stop():
    result = task_registry.dispatch(['{"type":"text","value":'], TASKS)

    assert result.startswith("ERRO")
    assert result != "stop"


def test_dispatch_sem_argumento_retorna_erro_e_nao_stop():
    result = task_registry.dispatch([], TASKS)

    assert result.startswith("ERRO")
    assert result != "stop"


def test_dispatch_mensagem_de_erro_lista_os_comandos_validos():
    result = task_registry.dispatch(['{"type":"text","value":"inexistente"}'], TASKS)

    assert "start" in result
    assert "classify" in result
    assert "finalize" in result


def test_dispatch_start_reinicia_o_contador_de_passos():
    for _ in range(5):
        task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)

    assert state_store.load().step == 5

    task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    # start reseta e então conta a si mesmo como passo 1.
    assert state_store.load().step == 1


def test_dispatch_start_com_context_persiste_no_state_store():
    task_registry.dispatch(
        ['{"type":"text","value":"start","context":{"driver":"claude code"}}'], TASKS
    )

    assert state_store.get_context()["driver"] == "claude code"


def test_dispatch_contexto_sobrevive_ao_start_e_e_reinjetado_via_prompt_formatter():
    tasks_com_prompt = {
        "start": lambda _e: prompt_formatter.format(
            "instrução", Envelope(EnvelopeType.COMMAND, "plan", ())
        ),
    }

    result = task_registry.dispatch(
        ['{"type":"text","value":"start","context":{"driver":"claude code"}}'], tasks_com_prompt
    )

    assert '"context":{"driver":"claude code"}' in result


def test_dispatch_ao_exceder_o_teto_forca_stop():
    # Consome exatamente o teto — todas essas ainda executam normalmente.
    for _ in range(task_registry.default_max_steps()):
        ok = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)
        assert ok != "stop"

    # O passo seguinte ultrapassa o teto e é cortado.
    result = task_registry.dispatch(['{"type":"tool","value":"classify","args":["x"]}'], TASKS)

    assert result == "stop"
