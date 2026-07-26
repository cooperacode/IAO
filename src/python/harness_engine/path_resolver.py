"""Resolve caminhos relativos ao diretório de trabalho (raiz do repo, de onde o driver
invoca o harness), com fallback para o diretório do pacote. Compartilhado por quem
injeta arquivos no prompt (skills, docs).
"""

from __future__ import annotations

import os
from pathlib import Path


def resolve(path: str) -> str:
    trimmed = path.strip()
    if Path(trimmed).is_absolute():
        return trimmed

    cwd = Path.cwd().resolve()
    from_cwd = (cwd / trimmed).resolve()
    if os.path.exists(from_cwd) and _is_contained(from_cwd, cwd):
        return str(from_cwd)

    base_dir = Path(__file__).resolve().parent
    from_base = (base_dir / trimmed).resolve()
    if _is_contained(from_base, base_dir):
        return str(from_base)

    # Nem o CWD nem o fallback do pacote contêm o resultado (symlink desviou para fora da
    # base) — devolve o join original, sem seguir o link; a checagem de existência do
    # chamador falha naturalmente sobre esse caminho não resolvido. Containment completo
    # contra uma raiz de política assinada é trabalho de fase futura (capability broker,
    # RFC §6.9) — isto é só a lista mínima de rejeição por symlink escape do RFC §6.3.
    return str(base_dir / trimmed)


def _is_contained(resolved: Path, base: Path) -> bool:
    """`resolved` está dentro de `base` por caminho REAL (não por prefixo de string) —
    segue symlinks (`.resolve()` já fez isso) e compara a árvore final."""
    try:
        return resolved == base or resolved.is_relative_to(base)
    except ValueError:
        return False
