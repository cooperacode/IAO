"""Data contract: parsing needs to tolerate what models actually return."""

from pathlib import Path

import pytest

from harness_engine.context_policy import ContextUsage
from harness_engine.envelope import Envelope, EnvelopeType


def test_parse_json_valido_preenche_os_tres_campos():
    envelope = Envelope.parse('{"type":"tool","value":"classify","args":["Login"]}')

    assert envelope is not None
    assert envelope.type == "tool"
    assert envelope.value == "classify"
    assert envelope.args == ("Login",)


def test_parse_com_cerca_markdown_tolera():
    raw = '```json\n{"type":"command","value":"finalize","args":["Bug"]}\n```'

    envelope = Envelope.parse(raw)

    assert envelope is not None
    assert envelope.value == "finalize"
    assert envelope.args == ("Bug",)


def test_parse_com_texto_ao_redor_extrai_o_objeto():
    raw = 'Sure! Here it is: {"type":"text","value":"start","args":[]} — hope that helps.'

    envelope = Envelope.parse(raw)

    assert envelope is not None
    assert envelope.value == "start"


def test_parse_sem_args_retorna_array_vazio():
    envelope = Envelope.parse('{"type":"text","value":"start"}')

    assert envelope is not None
    assert envelope.args == ()


def test_parse_ignora_args_vazios_ou_em_branco():
    envelope = Envelope.parse('{"type":"tool","value":"x","args":["a","","  ","b"]}')

    assert envelope is not None
    assert envelope.args == ("a", "b")


@pytest.mark.parametrize(
    "raw",
    [
        "",
        "   ",
        '{ "type": "text", "value": ',  # truncated JSON
        "this is not json",
        "[1,2,3]",  # not an object
    ],
)
def test_parse_entrada_invalida_retorna_none(raw):
    assert Envelope.parse(raw) is None


def test_parse_entrada_invalida_grava_o_payload_cru_no_harness_log():
    # The raw driver payload is otherwise lost forever — the inbox file gets overwritten
    # by the next attempt before anyone can inspect what actually failed.
    Envelope.parse("this is not json")

    content = Path(".harness", "harness.log").read_text()
    assert "this is not json" in content


def test_parse_payload_maior_que_o_teto_trunca_no_harness_log():
    oversized = "x" * 600 + "not json"

    Envelope.parse(oversized)

    content = Path(".harness", "harness.log").read_text()
    assert "...(truncated)" in content
    assert oversized not in content


def test_to_json_faz_roundtrip():
    original = Envelope(EnvelopeType.COMMAND, "finalize", ("Epic",))

    roundtrip = Envelope.parse(original.to_json())

    assert roundtrip == original


def test_parse_com_context_preenche_o_dicionario():
    envelope = Envelope.parse('{"type":"text","value":"start","context":{"driver":"claude code"}}')

    assert envelope is not None
    assert envelope.context["driver"] == "claude code"


def test_parse_sem_context_retorna_none():
    envelope = Envelope.parse('{"type":"text","value":"start"}')

    assert envelope is not None
    assert envelope.context is None


def test_to_json_com_context_faz_roundtrip():
    original = Envelope(EnvelopeType.TEXT, "start", (), context={"driver": "claude code"})

    roundtrip = Envelope.parse(original.to_json())

    assert roundtrip == original
    assert roundtrip.context["driver"] == "claude code"


def test_to_json_sem_context_nao_emite_o_campo():
    envelope = Envelope(EnvelopeType.COMMAND, "finalize", ("Epic",))

    assert "context" not in envelope.to_json()


def test_context_usage_faz_roundtrip():
    original = Envelope(
        EnvelopeType.COMMAND,
        "start",
        (),
        context_usage=ContextUsage("iao.context.v1", "s1", 128000, 84000, "driver"),
    )

    assert Envelope.parse(original.to_json()) == original
