"""Artefatos separados por arquivo + manifesto: a ordem de gravação é a ordem de leitura
(o juiz recebe as seções na sequência do flow), e o template dá a forma sem código."""

from pathlib import Path

from harness_engine import artifact_store, artifact_template


def test_write_grava_o_arquivo_e_registra_no_manifesto():
    path = artifact_store.write("historias", "# Histórias\n\n1. a")

    assert Path(path).exists()
    assert artifact_store.files() == [path]


def test_write_mesmo_nome_duas_vezes_sobrescreve_sem_duplicar_no_manifesto():
    artifact_store.write("historias", "v1")
    path = artifact_store.write("historias", "v2")

    assert len(artifact_store.files()) == 1
    assert Path(path).read_text() == "v2"


def test_read_all_concatena_na_ordem_de_gravacao():
    artifact_store.write("item", "# Item")
    artifact_store.write("historias", "# Histórias")

    all_content = artifact_store.read_all()

    assert all_content.index("# Item") < all_content.index("# Histórias")


def test_reset_apaga_artefatos_e_manifesto():
    path = artifact_store.write("historias", "x")

    artifact_store.reset()

    assert not Path(path).exists()
    assert not artifact_store.has_artifacts()
    assert artifact_store.files() == []


def test_render_substitui_placeholders_e_mantem_os_desconhecidos():
    result = artifact_template.render(
        "# {{titulo}}\n\n{{corpo}}\n\n{{sem_valor}}",
        {"titulo": "Riscos", "corpo": "lista"},
    )

    assert "# Riscos" in result
    assert "lista" in result
    assert "{{sem_valor}}" in result  # dado faltante fica visível, não some
