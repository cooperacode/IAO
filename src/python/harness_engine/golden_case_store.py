"""Loads the golden set cases from disk."""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class GoldenCase:
    """One golden set case: the expectation the recorded evidence is measured against.
    `expect_pass = False` marks an INTENTIONAL NEGATIVE case — a run that MUST fail on
    the metrics (e.g. a perfect trajectory but missing content), used to prove the
    evaluators catch the failure. The default is `True`."""

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
        print(f"[GoldenCaseStore] failed to load {path}: {ex}", file=sys.stderr)
        return None


def load_directory(directory: str) -> list[GoldenCase]:
    """Loads every `*.json` in a directory, sorted by name, skipping invalid ones."""
    d = Path(directory)
    if not d.is_dir():
        return []

    cases: list[GoldenCase] = []
    for path in sorted(d.glob("*.json"), key=lambda p: str(p)):
        case = load(str(path))
        if case is not None:
            cases.append(case)
    return cases
