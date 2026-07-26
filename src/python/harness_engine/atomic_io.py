"""Escrita atômica de arquivos texto: grava num temporário no MESMO diretório do destino e
troca via `os.replace` (atômico na mesma partição, tanto em POSIX quanto em Windows). Evita
que uma falha a meio da escrita (processo morto, disco cheio) deixe o arquivo de estado
autoritativo truncado/corrompido — o leitor sempre vê a versão anterior completa ou a nova
completa, nunca uma mistura.
"""

from __future__ import annotations

import os
import uuid
from pathlib import Path


def write_text_atomic(path: str, content: str) -> None:
    """Grava `content` em `path` de forma atômica. O diretório-pai deve existir."""
    destination = Path(path)
    tmp_path = destination.with_name(f"{destination.name}.tmp-{uuid.uuid4().hex}")

    try:
        tmp_path.write_text(content)
        os.replace(tmp_path, destination)
    except BaseException:
        tmp_path.unlink(missing_ok=True)
        raise
