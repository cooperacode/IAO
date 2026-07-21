from pathlib import Path

from harness_engine import prompt_formatter, state_store
from harness_engine.envelope import Envelope, EnvelopeType


def test_skills_aceita_varios_nomes_retorna_todos_os_mapeamentos():
    skills = prompt_formatter.skills("agile-workitem", "story-splitting")

    assert len(skills) == 2
    assert skills["agile-workitem"] == str(Path("skills") / "agile-workitem" / "SKILL.md")
    assert skills["story-splitting"] == str(Path("skills") / "story-splitting" / "SKILL.md")


def test_format_contexto_persistido_e_reinjetado_no_envelope_de_saida():
    state_store.set_context({"driver": "claude code"})
    output = Envelope(EnvelopeType.COMMAND, "plan", ())

    result = prompt_formatter.format("faça algo", output)

    assert '"context":{"driver":"claude code"}' in result


def test_format_sem_contexto_persistido_nao_emite_o_campo():
    output = Envelope(EnvelopeType.COMMAND, "plan", ())

    result = prompt_formatter.format("faça algo", output)

    assert "context" not in result


def test_format_contexto_ja_definido_na_task_nao_e_sobrescrito():
    state_store.set_context({"driver": "claude code"})
    output = Envelope(EnvelopeType.COMMAND, "plan", (), context={"driver": "explicito"})

    result = prompt_formatter.format("faça algo", output)

    assert "explicito" in result
    assert "claude code" not in result
