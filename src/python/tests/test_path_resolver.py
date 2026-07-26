"""path_resolver não tem teste dedicado no lado .NET, mas é usado por prompt_formatter,
docs_reader e harness_config para achar arquivos relativos ao cwd — vale uma cobertura
mínima direta."""

import os
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


def test_resolve_symlink_que_escapa_do_cwd_nao_devolve_o_caminho_desviado(tmp_path):
    # Um symlink dentro do cwd que aponta para fora dele (RFC §6.3 — path escape via
    # symlink). O resultado NÃO pode apontar para o arquivo real fora da base contra a
    # qual foi resolvido, mesmo que ele exista lá — senão o containment é só de fachada.
    outside = tmp_path.parent / f"outside-{tmp_path.name}"
    outside.mkdir()
    secret = outside / "secret.txt"
    secret.write_text("fora do cwd")

    os.symlink(outside, Path("escape"))

    resolved = path_resolver.resolve("escape/secret.txt")

    assert resolved != str(secret.resolve())
    assert not os.path.exists(resolved)  # cai no fallback do pacote, onde o arquivo não existe


def test_resolve_symlink_que_fica_dentro_do_cwd_resolve_normalmente(tmp_path):
    # Symlink que não escapa a base (aponta para outro lugar DENTRO do cwd) continua
    # funcionando normalmente — o containment barra escape, não todo symlink.
    real_dir = Path("real")
    real_dir.mkdir()
    (real_dir / "arquivo.txt").write_text("dentro")

    os.symlink(real_dir.resolve(), Path("link"))

    resolved = path_resolver.resolve("link/arquivo.txt")

    assert resolved == str((tmp_path / "real" / "arquivo.txt").resolve())
