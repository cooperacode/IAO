"""Isolamento de teste: cada teste roda num `tmp_path` próprio (chdir), em vez do
`[assembly: CollectionBehavior(DisableTestParallelization = true)]` do lado C# (necessário
lá porque os stores usam caminho fixo relativo ao cwd compartilhado entre testes). Aqui o
isolamento é real por teste, não só serialização — os testes ficam livres para rodar em
paralelo (ex.: pytest-xdist) se algum dia for preciso.
"""

from __future__ import annotations

import pytest

from harness_engine import harness_config


@pytest.fixture(autouse=True)
def isolated_cwd(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    # Em C# cada invocação do harness é um processo novo (o cache de HarnessConfig.Current
    # dura naturalmente 1 dispatch); num processo pytest de longa vida isso não vale de
    # graça — sem isto, o config carregado no primeiro teste vazaria para os seguintes.
    harness_config.reset()
    yield
    harness_config.reset()
