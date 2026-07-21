"""Transporte por inbox: com argv vazio, o dispatch lê o envelope de
`.harness/inbox.json` — o canal que elimina o hang de aspas do shell (o driver escreve um
arquivo em vez de montar um argumento single-quoted). Argv continua tendo precedência
(retrocompatível)."""

from pathlib import Path

from harness_engine import inbox, task_registry

TASKS = {
    "start": lambda _e: "PROMPT_START",
    "classify": lambda e: f"PROMPT_CLASSIFY:{e.args[0] if e and e.args else ''}",
}


def _write_inbox(json_text: str) -> None:
    Path(".harness").mkdir(exist_ok=True)
    Path(inbox.PATH).write_text(json_text)


def test_dispatch_sem_argumento_le_envelope_da_inbox():
    _write_inbox('{ "type": "text", "value": "start" }')

    result = task_registry.dispatch([], TASKS)

    assert result == "PROMPT_START"


def test_dispatch_da_inbox_preserva_os_args():
    # O caso que travava o shell: payload com aspas simples e quebras de linha. Via
    # arquivo, chega íntegro sem escaping frágil.
    _write_inbox('{ "type": "command", "value": "classify", "args": ["exportar \'PDF\'\\ne \'CSV\'"] }')

    result = task_registry.dispatch([], TASKS)

    assert result == "PROMPT_CLASSIFY:exportar 'PDF'\ne 'CSV'"


def test_dispatch_da_inbox_consome_o_arquivo_apos_parse():
    _write_inbox('{ "type": "text", "value": "start" }')

    task_registry.dispatch([], TASKS)

    assert not Path(inbox.PATH).exists(), "a inbox deve ser movida após um parse bem-sucedido"
    assert Path(inbox.CONSUMED_PATH).exists(), "o envelope consumido deve ficar como rastro"


def test_dispatch_inbox_invalida_retorna_erro_e_nao_consome():
    _write_inbox('{ "type": "text", "value": ')

    result = task_registry.dispatch([], TASKS)

    assert result.startswith("ERRO")
    # JSON quebrado permanece disponível para inspeção — não some silenciosamente.
    assert Path(inbox.PATH).exists(), "uma inbox que não parseia não deve ser consumida"


def test_dispatch_argv_tem_precedencia_sobre_inbox():
    # Argv presente → transporte clássico; a inbox é ignorada e permanece intacta.
    _write_inbox('{ "type": "command", "value": "classify", "args": ["da-inbox"] }')

    result = task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    assert result == "PROMPT_START"
    assert Path(inbox.PATH).exists(), "com argv, a inbox não deve ser tocada"
