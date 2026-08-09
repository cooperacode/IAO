"""Append-only evidence that may justify a global plan revision, persisted to
`.harness/plan_observations.jsonl` (JSON Lines — one observation per line). Appended by
`flows_development.tasks._handle_verify_failure` when deterministic verification keeps
failing; a later replan proposal cites these ids as its evidence
(`PlanRevision.based_on_observation_ids`), and `plan_revision_evaluator.evaluate` rejects
any id it can't find here. Never rewritten: each line is immutable, append-only evidence.

Same tolerance as the other stores: absent or unreadable → empty list, never brings the
run down. A corrupt final line (partial write, killed mid-append) is skipped, not fatal —
the file may have more valid lines before it.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

from harness_engine import harness_log

_FILE_PATH = ".harness/plan_observations.jsonl"


@dataclass(frozen=True)
class PlanObservation:
    """One piece of evidence: what kind of thing was observed, which feature (if any) it
    concerns, a short human summary, and the raw evidence strings backing it up."""

    id: str
    kind: str
    feature_id: int | None
    summary: str
    evidence: tuple[str, ...]
    observed_at: str

    def to_dict(self) -> dict[str, object]:
        return {
            "id": self.id,
            "kind": self.kind,
            "featureId": self.feature_id,
            "summary": self.summary,
            "evidence": list(self.evidence),
            "observedAt": self.observed_at,
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "PlanObservation":
        feature_id_raw = payload.get("featureId")
        evidence_raw = payload.get("evidence")
        return PlanObservation(
            id=str(payload.get("id") or ""),
            kind=str(payload.get("kind") or ""),
            feature_id=int(feature_id_raw) if isinstance(feature_id_raw, (int, float)) else None,
            summary=str(payload.get("summary") or ""),
            evidence=tuple(str(x) for x in evidence_raw) if isinstance(evidence_raw, list) else (),
            observed_at=str(payload.get("observedAt") or ""),
        )


def append(kind: str, feature_id: int | None, summary: str, *evidence: str) -> PlanObservation:
    """Appends and returns a new observation. Id is `OBS-{running count + 1, zero-padded
    to 3 digits}` — e.g. `OBS-001`, `OBS-002` — derived from the current line count, so ids
    stay contiguous even across process restarts."""
    observation = PlanObservation(
        id=f"OBS-{len(load()) + 1:03d}",
        kind=kind,
        feature_id=feature_id,
        summary=summary,
        evidence=tuple(e for e in evidence if e and e.strip()),
        observed_at=datetime.now(timezone.utc).isoformat(),
    )
    Path(".harness").mkdir(parents=True, exist_ok=True)
    with open(_FILE_PATH, "a", encoding="utf-8") as fh:
        fh.write(json.dumps(observation.to_dict()) + "\n")
    return observation


def load() -> list[PlanObservation]:
    path = Path(_FILE_PATH)
    if not path.exists():
        return []

    result: list[PlanObservation] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        try:
            result.append(PlanObservation.from_dict(json.loads(line)))
        except Exception:
            pass  # tolerate a partial final line after interruption
    return result


def reset() -> None:
    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        harness_log.error(f"[PlanObservationStore] failed to reset: {ex}")
