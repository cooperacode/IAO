"""Pure, deterministic gate for global plan revisions. It judges evidence and invariants
only; it does not interpret whether a technical strategy is semantically good — that
judgement is left to the driver, whose alternatives/reasoning this only requires to be
present and traceable. The only caller is `flows_development.tasks.replan`.

`evaluate` is soft: even an `Approve`d revision must still clear the hard domain
invariants enforced by `feature_store.apply_revision` (passed features are immutable,
dependency graph must resolve) before it's actually applied — the two gates are
deliberately independent so neither silently subsumes the other.
"""

from __future__ import annotations

from collections import deque
from collections.abc import Callable, Iterable
from dataclasses import dataclass, field

from harness_engine import plan_revision_store
from harness_engine.feature_store import Feature, PlanRevision
from harness_engine.plan_observation_store import PlanObservation


class PlanRevisionVerdict:
    """Possible outcomes of `evaluate`, recorded in `PlanRevisionEvaluation.verdict`."""

    APPROVE = "approve"
    APPROVE_WITH_WARNINGS = "approve_with_warnings"
    REJECT = "reject"


@dataclass(frozen=True)
class PlanRevisionIssue:
    code: str
    message: str

    def to_dict(self) -> dict[str, str]:
        return {"code": self.code, "message": self.message}

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "PlanRevisionIssue":
        return PlanRevisionIssue(str(payload.get("code") or ""), str(payload.get("message") or ""))


@dataclass(frozen=True)
class PlanDiff:
    added: tuple[int, ...] = ()
    removed: tuple[int, ...] = ()
    modified: tuple[int, ...] = ()
    reprioritized: tuple[int, ...] = ()
    removed_references: tuple[str, ...] = ()
    removed_acceptance_criteria: tuple[str, ...] = ()

    @property
    def has_changes(self) -> bool:
        return bool(self.added or self.removed or self.modified or self.reprioritized)

    def to_dict(self) -> dict[str, object]:
        return {
            "added": list(self.added),
            "removed": list(self.removed),
            "modified": list(self.modified),
            "reprioritized": list(self.reprioritized),
            "removedReferences": list(self.removed_references),
            "removedAcceptanceCriteria": list(self.removed_acceptance_criteria),
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "PlanDiff":
        def ints(key: str) -> tuple[int, ...]:
            raw = payload.get(key)
            return tuple(int(x) for x in raw) if isinstance(raw, list) else ()

        def strs(key: str) -> tuple[str, ...]:
            raw = payload.get(key)
            return tuple(str(x) for x in raw) if isinstance(raw, list) else ()

        return PlanDiff(
            ints("added"), ints("removed"), ints("modified"), ints("reprioritized"),
            strs("removedReferences"), strs("removedAcceptanceCriteria"),
        )


@dataclass(frozen=True)
class PlanRevisionEvaluation:
    verdict: str
    errors: tuple[PlanRevisionIssue, ...] = ()
    warnings: tuple[PlanRevisionIssue, ...] = ()
    diff: PlanDiff = field(default_factory=PlanDiff)

    @property
    def passed(self) -> bool:
        return self.verdict != PlanRevisionVerdict.REJECT

    def to_dict(self) -> dict[str, object]:
        return {
            "verdict": self.verdict,
            "errors": [e.to_dict() for e in self.errors],
            "warnings": [w.to_dict() for w in self.warnings],
            "diff": self.diff.to_dict(),
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "PlanRevisionEvaluation":
        errors_raw = payload.get("errors")
        warnings_raw = payload.get("warnings")
        diff_raw = payload.get("diff")
        return PlanRevisionEvaluation(
            verdict=str(payload.get("verdict") or PlanRevisionVerdict.REJECT),
            errors=tuple(PlanRevisionIssue.from_dict(e) for e in errors_raw) if isinstance(errors_raw, list) else (),
            warnings=(
                tuple(PlanRevisionIssue.from_dict(w) for w in warnings_raw) if isinstance(warnings_raw, list) else ()
            ),
            diff=PlanDiff.from_dict(diff_raw) if isinstance(diff_raw, dict) else PlanDiff(),
        )


def evaluate(
    current: list[Feature],
    revision: PlanRevision,
    observations: list[PlanObservation],
    max_features: int,
    remaining_steps: int,
    steps_per_feature: int,
) -> PlanRevisionEvaluation:
    errors: list[PlanRevisionIssue] = []
    warnings: list[PlanRevisionIssue] = []

    def error(code: str, message: str) -> None:
        errors.append(PlanRevisionIssue(code, message))

    proposed = list(revision.features)
    current_by_id = {f.id: f for f in current}
    proposed_by_id: dict[int, Feature] = {}
    for f in proposed:
        proposed_by_id.setdefault(f.id, f)  # first wins, mirrors GroupBy(...).First()
    diff = _build_diff(current, proposed)

    if not revision.reason.strip():
        error("REVISION_REASON_REQUIRED", "A revision reason is required.")

    alternatives = list(dict.fromkeys(_normalize(a) for a in revision.alternatives if a.strip()))
    if len(alternatives) < 2:
        error("ALTERNATIVES_REQUIRED", "At least two distinct alternatives are required.")

    if len(proposed) == 0:
        error("PLAN_EMPTY", "The revised plan must contain features.")
    if len(proposed) > max_features:
        error("FEATURE_LIMIT", f"The revised plan exceeds the {max_features}-feature limit.")
    if any(f.id <= 0 or not f.title.strip() or f.priority <= 0 for f in proposed):
        error("FEATURE_INVALID", "Every feature needs a positive unique id, title and positive priority.")
    if len({f.id for f in proposed}) != len(proposed):
        error("FEATURE_ID_DUPLICATE", "Feature ids must be unique.")

    for passed in (f for f in current if f.passes):
        retained = proposed_by_id.get(passed.id)
        if retained is None:
            error("PASSED_FEATURE_REMOVED", f"Passed feature #{passed.id} cannot be removed.")
        elif not _same_definition(passed, retained):
            error("PASSED_FEATURE_MODIFIED", f"Passed feature #{passed.id} cannot be modified.")

    passed_ids = {f.id for f in current if f.passes}
    _validate_graph(proposed, passed_ids, error)

    for reference in diff.removed_references:
        error("REQUIREMENT_COVERAGE_REMOVED", f"Brief reference '{reference}' is no longer covered.")
    for acceptance in diff.removed_acceptance_criteria:
        error("ACCEPTANCE_REMOVED", f"Acceptance criterion '{acceptance}' is no longer covered.")

    known_observation_ids = {o.id for o in observations}
    if len(revision.observation_ids) == 0:
        error("OBSERVATION_REQUIRED", "The revision must cite at least one persisted observation.")
    for observation_id in dict.fromkeys(revision.observation_ids):
        if observation_id not in known_observation_ids:
            error("OBSERVATION_UNKNOWN", f"Observation '{observation_id}' does not exist in the run evidence.")

    if not diff.has_changes:
        error("PLAN_UNCHANGED", "The revision does not change the current plan.")
    fp = plan_revision_store.fingerprint(proposed)
    if plan_revision_store.has_plan_fingerprint(fp):
        error("PLAN_REPEATED", "The same revised plan was already applied in this run.")

    pending = sum(1 for f in proposed if f.id not in current_by_id or not current_by_id[f.id].passes)
    worst_case_steps = pending * steps_per_feature
    if remaining_steps >= 0 and worst_case_steps > remaining_steps:
        warnings.append(PlanRevisionIssue(
            "BUDGET_RISK",
            f"The revised plan may require {worst_case_steps} steps with {remaining_steps} remaining.",
        ))

    verdict = (
        PlanRevisionVerdict.REJECT if errors
        else PlanRevisionVerdict.APPROVE_WITH_WARNINGS if warnings
        else PlanRevisionVerdict.APPROVE
    )
    return PlanRevisionEvaluation(verdict, tuple(errors), tuple(warnings), diff)


def _build_diff(current: list[Feature], proposed: list[Feature]) -> PlanDiff:
    before = {f.id: f for f in current}
    after: dict[int, Feature] = {}
    for f in proposed:
        after.setdefault(f.id, f)

    added = tuple(sorted(set(after) - set(before)))
    removed = tuple(sorted(set(before) - set(after)))
    common = set(before) & set(after)
    reprioritized = tuple(sorted(fid for fid in common if before[fid].priority != after[fid].priority))
    modified = tuple(sorted(fid for fid in common if not _same_definition_except_priority(before[fid], after[fid])))

    old_refs = _dedupe_case_insensitive(r for f in current for r in f.refs)
    new_ref_keys = {r.casefold() for f in proposed for r in f.refs if r.strip()}
    old_acceptance = _dedupe_case_insensitive(_normalize(a) for f in current for a in f.context.acceptance)
    new_acceptance_keys = {_normalize(a).casefold() for f in proposed for a in f.context.acceptance if a.strip()}

    removed_references = tuple(sorted(v for k, v in old_refs.items() if k not in new_ref_keys))
    removed_acceptance_criteria = tuple(sorted(v for k, v in old_acceptance.items() if k not in new_acceptance_keys))

    return PlanDiff(added, removed, modified, reprioritized, removed_references, removed_acceptance_criteria)


def _validate_graph(features: list[Feature], passed_ids: set[int], error: Callable[[str, str], None]) -> None:
    if len({f.id for f in features}) != len(features):
        return  # duplicate ids already reported by FEATURE_ID_DUPLICATE

    ids = {f.id for f in features}
    for feature in features:
        if feature.id in feature.deps:
            error("SELF_DEPENDENCY", f"Feature #{feature.id} depends on itself.")
        for missing in feature.deps:
            if missing not in ids:
                error("DEPENDENCY_MISSING", f"Feature #{feature.id} depends on missing feature #{missing}.")
    if any(dep not in ids for f in features for dep in f.deps):
        return

    indegree = {f.id: len(set(f.deps)) for f in features}
    dependents: dict[int, list[int]] = {}
    for f in features:
        for dep in set(f.deps):
            dependents.setdefault(dep, []).append(f.id)

    queue: deque[int] = deque(fid for fid, deg in indegree.items() if deg == 0)
    resolved = 0
    while queue:
        fid = queue.popleft()
        resolved += 1
        for dependent in dependents.get(fid, []):
            indegree[dependent] -= 1
            if indegree[dependent] == 0:
                queue.append(dependent)

    if resolved != len(features):
        error("DEPENDENCY_CYCLE", "The revised dependency graph contains a cycle.")

    pending = [f for f in features if f.id not in passed_ids]
    if pending and not any(all(dep in passed_ids for dep in f.deps) for f in pending):
        error("PLAN_NO_READY_FEATURE", "The revised plan has pending work but no executable feature.")


def _dedupe_case_insensitive(values: Iterable[str]) -> dict[str, str]:
    """Case-insensitive dedup that keeps the first-seen original casing — mirrors a
    `HashSet<string>(StringComparer.OrdinalIgnoreCase)`, which Python has no direct
    equivalent for."""
    result: dict[str, str] = {}
    for value in values:
        if value.strip():
            result.setdefault(value.casefold(), value)
    return result


def _same_definition(left: Feature, right: Feature) -> bool:
    return left.priority == right.priority and _same_definition_except_priority(left, right)


def _same_definition_except_priority(left: Feature, right: Feature) -> bool:
    return (
        left.id == right.id
        and left.title == right.title
        and left.description == right.description
        and left.deps == right.deps
        and left.refs == right.refs
        and left.context.requirements == right.context.requirements
        and left.context.constraints == right.context.constraints
        and left.context.files == right.context.files
        and left.context.acceptance == right.context.acceptance
    )


def _normalize(value: str) -> str:
    return " ".join(value.split())
