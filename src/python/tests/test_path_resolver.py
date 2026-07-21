"""path_resolver não tem teste dedicado no lado .NET, mas é usado por prompt_formatter,
docs_reader e harness_config para achar arquivos relativos ao cwd — vale uma cobertura
mínima direta."""

from pathlib import Path

from harness_engine import path_resolver


def test_resolve_caminho_absoluto_retorna_o_mesmo_caminho(tmp_path):
    absolute = str(tmp_path / "harness.json")

    assert path_resolver.resolve(absolute) == absolute


def test_resolve_caminho_relativo_existente_resolve_a_partir_do_cwd():
    Path("harness.json").write_text("{}")

    resolved = path_resolver.resolve("harness.json")

    assert resolved == str((Path.cwd() / "harness.json").resolve())


def test_resolve_caminho_relativo_inexistente_cai_no_fallback_do_pacote():
    resolved = path_resolver.resolve("um-arquivo-que-nao-existe.md")

    # Não existindo em lugar nenhum, cai no fallback relativo ao pacote (nunca lança) —
    # não fica preso ao cwd do teste, que é justamente o caso em que nada existe lá.
    package_dir = Path(path_resolver.__file__).resolve().parent
    assert resolved == str((package_dir / "um-arquivo-que-nao-existe.md").resolve())
