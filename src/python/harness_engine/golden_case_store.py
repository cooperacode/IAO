"""Carrega os casos do golden set do disco."""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class GoldenCase:
    """Um caso do golden set: o esperado contra o qual a evidência gravada é medida.
    `expect_pass = False` marca um caso NEGATIVO INTENCIONAL — um run que DEVE reprovar
    nas métricas (ex.: trajetória perfeita mas conteúdo faltante), usado para provar que
    os evaluators pegam a falha. O padrão é `True`."""

    id: str
    description: str
    expected_trajectory: tuple[str, ...]
    required_keys: tuple[str, ...]
    expect_pass: bool = True

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "GoldenCase":
        return GoldenCase(
            id=str(payload.get("id") or ""),
            description=str(payload.get("description") or ""),
            expected_trajectory=tuple(payload.get("expectedTrajectory") or ()),
            required_keys=tuple(payload.get("requiredKeys") or ()),
            expect_pass=bool(payload.get("expectPass", True)),
        )


def load(path: str) -> GoldenCase | None:
    try:
        return GoldenCase.from_dict(json.loads(Path(path).read_text()))
    except Exception as ex:
        print(f"[GoldenCaseStore] falha ao carregar {path}: {ex}", file=sys.stderr)
        return None


def load_directory(directory: str) -> list[GoldenCase]:
    """Carrega todos os `*.json` de um diretório, ordenados por nome, ignorando os inválidos."""
    d = Path(directory)
    if not d.is_dir():
        return []

    cases: list[GoldenCase] = []
    for path in sorted(d.glob("*.json"), key=lambda p: str(p)):
        case = load(str(path))
        if case is not None:
            cases.append(case)
    return cases
