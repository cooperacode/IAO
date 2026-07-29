"""Inbox transport: with empty argv, dispatch reads the envelope from
`.harness/inbox.json` — the channel that eliminates the shell-quoting hang (the driver
writes a file instead of assembling a single-quoted argument). Argv still takes
precedence (backward compatible)."""

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
    # The case that used to hang the shell: a payload with single quotes and line
    # breaks. Via file, it arrives intact without fragile escaping.
    _write_inbox('{ "type": "command", "value": "classify", "args": ["export \'PDF\'\\nand \'CSV\'"] }')

    result = task_registry.dispatch([], TASKS)

    assert result == "PROMPT_CLASSIFY:export 'PDF'\nand 'CSV'"


def test_dispatch_da_inbox_consome_o_arquivo_apos_parse():
    _write_inbox('{ "type": "text", "value": "start" }')

    task_registry.dispatch([], TASKS)

    assert not Path(inbox.PATH).exists(), "the inbox should be moved after a successful parse"
    assert Path(inbox.CONSUMED_PATH).exists(), "the consumed envelope should remain as a trail"


def test_dispatch_inbox_invalida_retorna_erro_e_nao_consome():
    _write_inbox('{ "type": "text", "value": ')

    result = task_registry.dispatch([], TASKS)

    assert result.startswith("HARNESS PROTOCOL ERROR")
    # Broken JSON remains available for inspection — it doesn't silently disappear.
    assert Path(inbox.PATH).exists(), "an inbox that fails to parse must not be consumed"


def test_dispatch_argv_tem_precedencia_sobre_inbox():
    # Argv present → classic transport; the inbox is ignored and stays intact.
    _write_inbox('{ "type": "command", "value": "classify", "args": ["from-inbox"] }')

    result = task_registry.dispatch(['{"type":"text","value":"start"}'], TASKS)

    assert result == "PROMPT_START"
    assert Path(inbox.PATH).exists(), "with argv, the inbox must not be touched"
