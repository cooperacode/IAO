"""docs_reader é a entrada alternativa ao input interativo: lê os documentos da pasta
(determinístico, em código) para que o modelo só precise sintetizar o brief."""

from pathlib import Path

from harness_engine import docs_reader


def test_has_docs_pasta_inexistente_false(tmp_path):
    d = tmp_path / "docs-inexistente"

    assert not docs_reader.has_docs(str(d))


def test_has_docs_pasta_vazia_false(tmp_path):
    d = tmp_path / "docs-vazia"
    d.mkdir()

    assert not docs_reader.has_docs(str(d))


def test_has_docs_ignora_extensoes_nao_suportadas(tmp_path):
    d = tmp_path / "docs"
    d.mkdir()
    (d / "imagem.png").write_text("x")
    (d / "dados.json").write_text("{}")

    assert not docs_reader.has_docs(str(d))


def test_has_docs_com_markdown_true(tmp_path):
    d = tmp_path / "docs"
    d.mkdir()
    (d / "spec.md").write_text("conteúdo")

    assert docs_reader.has_docs(str(d))


def test_read_concatena_md_e_txt_em_ordem_alfabetica(tmp_path):
    d = tmp_path / "docs"
    d.mkdir()
    (d / "b-notas.txt").write_text("notas")
    (d / "a-spec.md").write_text("spec")

    content, files = docs_reader.read(str(d))

    assert files == ["a-spec.md", "b-notas.txt"]
    assert "## a-spec.md" in content
    assert "## b-notas.txt" in content
    assert content.index("a-spec.md") < content.index("b-notas.txt")


def test_read_pasta_inexistente_vazio_sem_fontes(tmp_path):
    d = tmp_path / "docs-inexistente"

    content, files = docs_reader.read(str(d))

    assert content == ""
    assert files == []


def test_read_conteudo_com_acento_e_emoji_nao_quebra_caractere_multibyte(tmp_path):
    # "café ☕" tem "é" (2 bytes) e "☕" (3 bytes) em UTF-8 — um corte ingênuo por posição
    # de byte no meio de qualquer um deles produziria uma sequência inválida.
    d = tmp_path / "docs"
    d.mkdir()
    (d / "a.md").write_text("café ☕ café ☕ café ☕")

    content, _ = docs_reader.read(str(d))

    assert "café ☕" in content
    # content já é um `str` Python válido (decode bem-sucedido) — se o corte tivesse
    # partido um caractere multibyte, decode(errors="ignore") já teria descartado o
    # fragmento inválido silenciosamente, então testamos que nada sobrou corrompido.
    assert "�" not in content
