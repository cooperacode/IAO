"""Persists the result of each evaluation to `.harness/scores.jsonl` (one line per run)."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from harness_engine import harness_log

_DIR = ".harness"
_FILE_PATH = ".harness/scores.jsonl"


@dataclass(frozen=True)
class ScoreReport:
    """Score for one evaluation: the deterministic gate's verdict and, when it passes,
    the LLM judge's score. `judge_score` = 0 when the gate fails."""

    timestamp: str
    gate_passed: bool
    gate_detail: str
    judge_score: int
    judge_rationale: str

    def to_dict(self) -> dict[str, object]:
        return {
            "timestamp": self.timestamp,
            "gatePassed": self.gate_passed,
            "gateDetail": self.gate_detail,
            "judgeScore": self.judge_score,
            "judgeRationale": self.judge_rationale,
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "ScoreReport":
        return ScoreReport(
            timestamp=str(payload.get("timestamp") or ""),
            gate_passed=bool(payload.get("gatePassed", False)),
            gate_detail=str(payload.get("gateDetail") or ""),
            judge_score=int(payload.get("judgeScore", 0) or 0),
            judge_rationale=str(payload.get("judgeRationale") or ""),
        )


def append(report: ScoreReport) -> None:
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        with open(_FILE_PATH, "a") as f:
            f.write(json.dumps(report.to_dict(), separators=(",", ":")) + "\n")
    except Exception as ex:
        harness_log.error(f"[ScoreStore] failed to write: {ex}")


def load() -> list[ScoreReport]:
    try:
        p = Path(_FILE_PATH)
        if not p.exists():
            return []

        return [
            ScoreReport.from_dict(json.loads(line))
            for line in p.read_text().splitlines()
            if line.strip()
        ]
    except Exception as ex:
        harness_log.error(f"[ScoreStore] failed to load: {ex}")
        return []
