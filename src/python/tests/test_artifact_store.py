"""Artifacts split by file + manifest: write order is read order (the judge receives the
sections in the flow's sequence), and the template gives the shape without code."""

from pathlib import Path

from harness_engine import artifact_store, artifact_template


def test_write_grava_o_arquivo_e_registra_no_manifesto():
    path = artifact_store.write("stories", "# Stories\n\n1. a")

    assert Path(path).exists()
    assert artifact_store.files() == [path]


def test_write_mesmo_nome_duas_vezes_sobrescreve_sem_duplicar_no_manifesto():
    artifact_store.write("stories", "v1")
    path = artifact_store.write("stories", "v2")

    assert len(artifact_store.files()) == 1
    assert Path(path).read_text() == "v2"


def test_read_all_concatena_na_ordem_de_gravacao():
    artifact_store.write("item", "# Item")
    artifact_store.write("stories", "# Stories")

    all_content = artifact_store.read_all()

    assert all_content.index("# Item") < all_content.index("# Stories")


def test_read_devolve_conteudo_gravado():
    artifact_store.write("brief", "# Brief\n\nBuild X.")

    assert artifact_store.read("brief") == "# Brief\n\nBuild X."


def test_read_devolve_vazio_quando_artefato_nao_existe():
    assert artifact_store.read("never-written") == ""


def test_reset_apaga_artefatos_e_manifesto():
    path = artifact_store.write("stories", "x")

    artifact_store.reset()

    assert not Path(path).exists()
    assert not artifact_store.has_artifacts()
    assert artifact_store.files() == []


def test_render_substitui_placeholders_e_mantem_os_desconhecidos():
    result = artifact_template.render(
        "# {{title}}\n\n{{body}}\n\n{{no_value}}",
        {"title": "Risks", "body": "list"},
    )

    assert "# Risks" in result
    assert "list" in result
    assert "{{no_value}}" in result  # missing data stays visible, doesn't disappear
