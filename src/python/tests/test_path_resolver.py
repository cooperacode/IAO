"""path_resolver doesn't have a dedicated test on the .NET side, but it's used by
prompt_formatter, docs_reader, and harness_config to find files relative to the cwd —
worth a minimal direct coverage."""

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

    # Not existing anywhere, it falls back to the package-relative path (never throws) —
    # it isn't tied to the test's cwd, which is precisely the case where nothing exists there.
    package_dir = Path(path_resolver.__file__).resolve().parent
    assert resolved == str((package_dir / "um-arquivo-que-nao-existe.md").resolve())


def test_resolve_symlink_que_escapa_do_cwd_nao_devolve_o_caminho_desviado(tmp_path):
    # A symlink inside the cwd that points outside it (RFC §6.3 — path escape via
    # symlink). The result must NOT point to the real file outside the base it was
    # resolved against, even if it exists there — otherwise containment is just for show.
    outside = tmp_path.parent / f"outside-{tmp_path.name}"
    outside.mkdir()
    secret = outside / "secret.txt"
    secret.write_text("outside the cwd")

    os.symlink(outside, Path("escape"))

    resolved = path_resolver.resolve("escape/secret.txt")

    assert resolved != str(secret.resolve())
    assert not os.path.exists(resolved)  # falls back to the package path, where the file doesn't exist


def test_resolve_symlink_que_fica_dentro_do_cwd_resolve_normalmente(tmp_path):
    # A symlink that doesn't escape the base (points elsewhere INSIDE the cwd) keeps
    # working normally — containment blocks escape, not every symlink.
    real_dir = Path("real")
    real_dir.mkdir()
    (real_dir / "arquivo.txt").write_text("inside")

    os.symlink(real_dir.resolve(), Path("link"))

    resolved = path_resolver.resolve("link/arquivo.txt")

    assert resolved == str((tmp_path / "real" / "arquivo.txt").resolve())
