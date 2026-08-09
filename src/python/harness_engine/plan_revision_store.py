"""Persists proposed and accepted global plan revisions for audit and resume.

Three paths, three lifecycles:
- `PROPOSAL_PATH` (`.harness/replan.json`) — the driver's unvalidated proposal, written by
  its own file-write tool (same convention as `state_keys.PLAN_FILE_PATH`) and read once by
  `flows_development.tasks.replan`.
- `_CURRENT_PATH` (`.harness/plan_revision.json`) — the latest ACCEPTED revision; its
  `version` field is this run's revision counter (0 if none applied yet).
- `_DIR` (`.harness/plans/plan-v{N}.json`) — the full accepted-revision history, one file
  per version, used only to fingerprint-dedupe a repeated proposal within the same run.
"""

from __future__ import annotations

import hashlib
import json
import shutil
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import TYPE_CHECKING

from harness_engine import harness_log
from harness_engine.atomic_io import write_text_atomic
from harness_engine.feature_store import Feature, PlanRevision

if TYPE_CHECKING:
    # Type-only: plan_revision_evaluator imports THIS module at runtime (to call
    # fingerprint/has_plan_fingerprint), so importing it back here at module load time
    # would cycle. record() only ever calls evaluation.to_dict() — duck typing, no runtime
    # import needed.
    from harness_engine.plan_revision_evaluator import PlanRevisionEvaluation

PROPOSAL_PATH = ".harness/replan.json"
_DIR = ".harness/plans"
_CURRENT_PATH = ".harness/plan_revision.json"


@dataclass(frozen=True)
class AppliedPlanRevision:
    """One accepted revision, as persisted to `_CURRENT_PATH` and `_DIR`."""

    version: int
    applied_at: str
    reason: str
    alternatives_considered: tuple[str, ...]
    based_on_observation_ids: tuple[str, ...]
    plan_fingerprint: str
    evaluation: "PlanRevisionEvaluation"
    features: tuple[Feature, ...]

    def to_dict(self) -> dict[str, object]:
        return {
            "version": self.version,
            "appliedAt": self.applied_at,
            "reason": self.reason,
            "alternativesConsidered": list(self.alternatives_considered),
            "basedOnObservationIds": list(self.based_on_observation_ids),
            "planFingerprint": self.plan_fingerprint,
            "evaluation": self.evaluation.to_dict(),
            "features": [f.to_dict() for f in self.features],
        }


def read_proposal() -> PlanRevision | None:
    try:
        path = Path(PROPOSAL_PATH)
        if not path.exists():
            return None
        return PlanRevision.from_dict(json.loads(path.read_text()))
    except Exception as ex:
        harness_log.error(f"[PlanRevisionStore] failed to read proposal: {ex}")
        return None


def revision_count() -> int:
    payload = _read_json(_CURRENT_PATH)
    try:
        return int(payload.get("version") or 0) if isinstance(payload, dict) else 0
    except (TypeError, ValueError):
        return 0


def record(revision: PlanRevision, features: list[Feature], evaluation: "PlanRevisionEvaluation") -> None:
    applied = AppliedPlanRevision(
        version=revision_count() + 1,
        applied_at=datetime.now(timezone.utc).isoformat(),
        reason=revision.reason,
        alternatives_considered=revision.alternatives,
        based_on_observation_ids=revision.observation_ids,
        plan_fingerprint=fingerprint(features),
        evaluation=evaluation,
        features=tuple(features),
    )
    Path(_DIR).mkdir(parents=True, exist_ok=True)
    payload = json.dumps(applied.to_dict(), indent=2)
    write_text_atomic(_CURRENT_PATH, payload)
    write_text_atomic(str(Path(_DIR) / f"plan-v{applied.version}.json"), payload)


def has_plan_fingerprint(fp: str) -> bool:
    directory = Path(_DIR)
    if not directory.exists():
        return False
    for path in directory.glob("plan-v*.json"):
        try:
            payload = json.loads(path.read_text())
        except Exception:
            continue  # corrupt history never approves a proposal
        if isinstance(payload, dict) and payload.get("planFingerprint") == fp:
            return True
    return False


def fingerprint(features: list[Feature]) -> str:
    """Sorts by id, forces `passes=False` in the canonical form (so a plan re-proposed
    after having already been fully implemented still fingerprints the same as when it was
    first proposed), serializes, SHA-256, lowercase hex."""
    canonical = sorted((replace(f, passes=False) for f in features), key=lambda f: f.id)
    payload = {"items": [f.to_dict() for f in canonical]}
    encoded = json.dumps(payload, separators=(",", ":"), sort_keys=True).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def reset() -> None:
    try:
        Path(PROPOSAL_PATH).unlink(missing_ok=True)
        Path(_CURRENT_PATH).unlink(missing_ok=True)
        directory = Path(_DIR)
        if directory.exists():
            shutil.rmtree(directory)
    except Exception as ex:
        harness_log.error(f"[PlanRevisionStore] failed to reset: {ex}")


def _read_json(path: str) -> dict[str, object] | None:
    try:
        p = Path(path)
        if not p.exists():
            return None
        payload = json.loads(p.read_text())
        return payload if isinstance(payload, dict) else None
    except Exception:
        return None
