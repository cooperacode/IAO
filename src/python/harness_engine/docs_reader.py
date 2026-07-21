"""Lê um conjunto de documentos (`*.md` e `*.txt`) de uma pasta para injetar no prompt.
É a entrada alternativa ao input interativo: o flow lê o material já existente (specs,
notas, transcrições) e o modelo sintetiza um brief a partir dele.

Análogo a como prompt_formatter injeta skills — a leitura é determinística (feita em
código), só a síntese fica com o modelo.
"""

from __future__ import annotations

import sys
from pathlib import Path

from harness_engine import harness_config, path_resolver

_EXTENSIONS = {".md", ".txt"}


def _max_chars() -> int:
    # Teto de caracteres: injetar docs gigantes queima tokens de forma silenciosa. Ao
    # exceder, trunca e avisa no stderr. Valor vem do harness.json (ou do default).
    return harness_config.current().docs_max_chars


def has_docs(folder: str) -> bool:
    """Existe a pasta e há ao menos um arquivo `*.md`/`*.txt`?"""
    directory = Path(path_resolver.resolve(folder))
    return directory.is_dir() and len(_files(directory)) > 0


def read(folder: str) -> tuple[str, list[str]]:
    """Concatena os documentos em ordem alfabética, cada um sob um cabeçalho
    `## <nome-do-arquivo>`, e devolve também a lista de nomes (para citar as fontes)."""
    directory = Path(path_resolver.resolve(folder))
    if not directory.is_dir():
        return "", []

    files = _files(directory)
    names: list[str] = []
    parts: list[str] = []
    total_len = 0
    max_chars = _max_chars()

    for path in files:
        try:
            text = path.read_text()
        except Exception as ex:
            print(f"[DocsReader] falha ao ler {path.name}: {ex}", file=sys.stderr)
            continue

        names.append(path.name)
        chunk = f"## {path.name}\n\n{text}\n\n"
        parts.append(chunk)
        total_len += len(chunk)

        if total_len > max_chars:
            print(f"[DocsReader] conteúdo excedeu {max_chars} chars; truncando em {path.name}.", file=sys.stderr)
            content = "".join(parts)[:max_chars]
            return content.rstrip(), names

    return "".join(parts).rstrip(), names


def _files(directory: Path) -> list[Path]:
    return sorted(
        (p for p in directory.iterdir() if p.is_file() and p.suffix.lower() in _EXTENSIONS),
        key=lambda p: p.name.lower(),
    )
