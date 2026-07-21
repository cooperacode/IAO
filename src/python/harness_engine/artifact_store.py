"""Persiste cada artefato do flow no seu próprio arquivo (`.harness/<nome>.md`) e mantém
um manifesto (`.harness/artifacts.json`) com a ordem de gravação. O manifesto é o
contrato entre produtor e consumidor: a avaliação lê os artefatos por ele, sem depender
de um relatório combinado.

Só o flow PRODUTOR reseta o manifesto (no seu `start`) — o consumidor (avaliação) não
toca nele, pela mesma razão dos snapshots de trace/state_store: o start do avaliador não
pode apagar a evidência que ele mesmo vai ler.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

_DIR = ".harness"
MANIFEST_PATH = ".harness/artifacts.json"


def reset() -> None:
    """Apaga os artefatos do run anterior e o manifesto — chamado pelo flow produtor no start."""
    try:
        for file in files():
            Path(file).unlink(missing_ok=True)
        Path(MANIFEST_PATH).unlink(missing_ok=True)
    except Exception as ex:
        print(f"[ArtifactStore] falha ao limpar: {ex}", file=sys.stderr)


def write(name: str, content: str) -> str:
    """Grava `.harness/<nome>.md` e registra o caminho no manifesto (uma vez, em ordem de chegada)."""
    path = str(Path(_DIR) / f"{name}.md")

    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        Path(path).write_text(content)

        current_files = list(files())
        if path not in current_files:
            current_files.append(path)
            _save_manifest(current_files)
    except Exception as ex:
        print(f"[ArtifactStore] falha ao gravar {name}: {ex}", file=sys.stderr)

    return path


def files() -> list[str]:
    """Caminhos registrados no manifesto, na ordem em que foram gravados."""
    try:
        p = Path(MANIFEST_PATH)
        if p.exists():
            payload = json.loads(p.read_text())
            result = payload.get("files") if isinstance(payload, dict) else None
            if isinstance(result, list):
                return [str(f) for f in result]
    except Exception as ex:
        print(f"[ArtifactStore] falha ao carregar manifesto: {ex}", file=sys.stderr)

    return []


def has_artifacts() -> bool:
    """Há artefatos gravados e presentes no disco?"""
    return any(Path(f).exists() for f in files())


def read_all() -> str:
    """Concatena os artefatos na ordem do manifesto — o insumo do juiz-LLM."""
    parts: list[str] = []

    for file in files():
        try:
            p = Path(file)
            if p.exists():
                parts.append(p.read_text().rstrip() + "\n")
        except Exception as ex:
            print(f"[ArtifactStore] falha ao ler {file}: {ex}", file=sys.stderr)

    return "".join(parts).rstrip()


def _save_manifest(file_list: list[str]) -> None:
    Path(_DIR).mkdir(parents=True, exist_ok=True)
    Path(MANIFEST_PATH).write_text(json.dumps({"files": file_list}, separators=(",", ":")))
