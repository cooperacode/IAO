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

    from_cwd = str((Path.cwd() / trimmed).resolve())
    if os.path.exists(from_cwd):
        return from_cwd

    base_dir = Path(__file__).resolve().parent
    return str((base_dir / trimmed).resolve())
