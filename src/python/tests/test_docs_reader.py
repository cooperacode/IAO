"""docs_reader is the alternative input to the interactive one: it reads the folder's
documents (deterministic, in code) so the model only needs to synthesize the brief."""

from pathlib import Path

from harness_engine import docs_reader


def test_has_docs_pasta_inexistente_false(tmp_path):
    d = tmp_path / "specs-inexistente"

    assert not docs_reader.has_docs(str(d))


def test_has_docs_pasta_vazia_false(tmp_path):
    d = tmp_path / "specs-vazia"
    d.mkdir()

    assert not docs_reader.has_docs(str(d))


def test_has_docs_ignora_extensoes_nao_suportadas(tmp_path):
    d = tmp_path / "specs"
    d.mkdir()
    (d / "image.png").write_text("x")
    (d / "data.json").write_text("{}")

    assert not docs_reader.has_docs(str(d))


def test_has_docs_com_markdown_true(tmp_path):
    d = tmp_path / "specs"
    d.mkdir()
    (d / "spec.md").write_text("content")

    assert docs_reader.has_docs(str(d))


def test_read_concatena_md_e_txt_em_ordem_alfabetica(tmp_path):
    d = tmp_path / "specs"
    d.mkdir()
    (d / "b-notas.txt").write_text("notes")
    (d / "a-spec.md").write_text("spec")

    content, files = docs_reader.read(str(d))

    assert files == ["a-spec.md", "b-notas.txt"]
    assert "## a-spec.md" in content
    assert "## b-notas.txt" in content
    assert content.index("a-spec.md") < content.index("b-notas.txt")


def test_read_pasta_inexistente_vazio_sem_fontes(tmp_path):
    d = tmp_path / "specs-inexistente"

    content, files = docs_reader.read(str(d))

    assert content == ""
    assert files == []


def test_read_conteudo_com_acento_e_emoji_nao_quebra_caractere_multibyte(tmp_path):
    # "café ☕" has "é" (2 bytes) and "☕" (3 bytes) in UTF-8 — a naive cut by byte
    # position in the middle of either would produce an invalid sequence.
    d = tmp_path / "specs"
    d.mkdir()
    (d / "a.md").write_text("café ☕ café ☕ café ☕")

    content, _ = docs_reader.read(str(d))

    assert "café ☕" in content
    # content is already a valid Python `str` (successful decode) — if the cut had split
    # a multi-byte character, decode(errors="ignore") would have already silently
    # discarded the invalid fragment, so we test that nothing corrupted was left over.
    assert "�" not in content
