"""Reads a set of documents (`*.md` and `*.txt`) from a folder to inject into the prompt.
It is the alternative input to the interactive one: the flow reads material that already
exists (specs, notes, transcripts) and the model synthesizes a brief from it.

Analogous to how prompt_formatter injects skills — the reading is deterministic (done in
code), only the synthesis is left to the model.
"""

from __future__ import annotations

import sys
from pathlib import Path

from harness_engine import harness_config, path_resolver

_EXTENSIONS = {".md", ".txt"}


def _max_chars() -> int:
    # Char ceiling: injecting giant docs silently burns tokens. When exceeded, truncates
    # and warns on stderr. Value comes from harness.json (or the default).
    return harness_config.current().docs_max_chars


def has_docs(folder: str) -> bool:
    """Does the folder exist and does it have at least one `*.md`/`*.txt` file?"""
    directory = Path(path_resolver.resolve(folder))
    return directory.is_dir() and len(_files(directory)) > 0


def read(folder: str) -> tuple[str, list[str]]:
    """Concatenates the documents in alphabetical order, each under a
    `## <file-name>` heading, and also returns the list of names (to cite the sources)."""
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
            print(f"[DocsReader] failed to read {path.name}: {ex}", file=sys.stderr)
            continue

        names.append(path.name)
        chunk = f"## {path.name}\n\n{text}\n\n"
        parts.append(chunk)
        total_len += len(chunk.encode("utf-8"))

        if total_len > max_chars:
            print(f"[DocsReader] content exceeded {max_chars} bytes (UTF-8); truncating at {path.name}.", file=sys.stderr)
            # Cuts at bytes, not codepoints (RFC Appendix B item 1): decoding with
            # errors="ignore" automatically discards any multibyte sequence cut in half
            # at the limit's edge.
            truncated_bytes = "".join(parts).encode("utf-8")[:max_chars]
            content = truncated_bytes.decode("utf-8", errors="ignore")
            return content.rstrip(), names

    return "".join(parts).rstrip(), names


def _files(directory: Path) -> list[Path]:
    return sorted(
        (p for p in directory.iterdir() if p.is_file() and p.suffix.lower() in _EXTENSIONS),
        key=lambda p: p.name.lower(),
    )
