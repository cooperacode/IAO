"""The development flow's feature list, persisted to `.harness/feature_list.json` — the
"persistent artifact" that survives hard context resets: each session (one feature) reads
and writes here, without depending on conversation history. All features are born with
`Feature.passes = False`; the flow turns one at a time until none remain pending.

Same tolerance as the other stores: absent or unreadable → empty list, never brings the
run down.
"""

from __future__ import annotations

import json
from collections import deque
from dataclasses import dataclass, replace
from pathlib import Path

from harness_engine import harness_log
from harness_engine.atomic_io import write_text_atomic

_DIR = ".harness"
_FILE_PATH = ".harness/feature_list.json"

# Ceiling on Feature.description chars — a defensive quota against a verbose driver: the
# description is reinjected into the implement prompt for every feature, so without a
# ceiling it silently inflates every future session's context.
DESCRIPTION_MAX_CHARS = 700
IMPLEMENTATION_CONTEXT_MAX_CHARS = 4000


@dataclass(frozen=True)
class ImplementationContext:
    """Inline implementation guidance grouped by purpose."""

    requirements: tuple[str, ...] = ()
    constraints: tuple[str, ...] = ()
    files: tuple[str, ...] = ()
    acceptance: tuple[str, ...] = ()

    @property
    def is_empty(self) -> bool:
        return not any((self.requirements, self.constraints, self.files, self.acceptance))

    def prompt_text(self) -> str:
        def format_items(label: str, values: tuple[str, ...]) -> str:
            escaped = (value.replace("\r\n", "\\n").replace("\n", "\\n") for value in values)
            return f"{label}: {'; '.join(escaped)}"

        return "\\n".join((
            format_items("requirements", self.requirements),
            format_items("constraints", self.constraints),
            format_items("files", self.files),
            format_items("acceptance", self.acceptance),
        ))


@dataclass(frozen=True)
class Feature:
    """One feature of the development backlog: priority (lower = higher), whether it
    already passes, which other ids it depends on, a free-form description (up to
    DESCRIPTION_MAX_CHARS chars, reinjected into the implement prompt), and explicit
    reference codes from the brief (e.g. "RF-003"; empty when the brief cites none).

    `depends_on`/`references` are NULLABLE on purpose: a `feature_list.json` written by an
    earlier version (without these keys) still loads without raising — `deps`/`refs`
    normalize it for consumers.
    """

    id: int
    title: str
    priority: int
    passes: bool
    depends_on: tuple[int, ...] | None = None
    description: str = ""
    references: tuple[str, ...] | None = None
    implementation_context: ImplementationContext | None = None

    @property
    def deps(self) -> tuple[int, ...]:
        return self.depends_on if self.depends_on is not None else ()

    @property
    def refs(self) -> tuple[str, ...]:
        return self.references if self.references is not None else ()

    @property
    def context(self) -> ImplementationContext:
        return self.implementation_context or ImplementationContext()

    def to_dict(self) -> dict[str, object]:
        return {
            "id": self.id,
            "title": self.title,
            "priority": self.priority,
            "passes": self.passes,
            "dependsOn": list(self.depends_on) if self.depends_on is not None else None,
            "description": self.description,
            "references": list(self.references) if self.references is not None else None,
            "implementationContext": {
                "requirements": list(self.context.requirements),
                "constraints": list(self.context.constraints),
                "files": list(self.context.files),
                "acceptance": list(self.context.acceptance),
            },
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "Feature":
        depends_on_raw = payload.get("dependsOn")
        depends_on = tuple(int(x) for x in depends_on_raw) if isinstance(depends_on_raw, list) else None
        references_raw = payload.get("references")
        references = tuple(str(x) for x in references_raw) if isinstance(references_raw, list) else None
        return Feature(
            id=int(payload.get("id") or 0),
            title=str(payload.get("title") or ""),
            priority=int(payload.get("priority") or 0),
            passes=bool(payload.get("passes", False)),
            depends_on=depends_on,
            description=str(payload.get("description") or ""),
            references=references,
            implementation_context=_implementation_context_from_payload(payload.get("implementationContext")),
        )


def _implementation_context_from_payload(value: object) -> ImplementationContext:
    if isinstance(value, str):
        return ImplementationContext(requirements=(value,)) if value.strip() else ImplementationContext()
    if not isinstance(value, dict):
        return ImplementationContext()

    def items(name: str) -> tuple[str, ...]:
        raw = value.get(name)
        return tuple(str(item) for item in raw if str(item).strip()) if isinstance(raw, list) else ()

    return ImplementationContext(items("requirements"), items("constraints"), items("files"), items("acceptance"))


def _truncate_implementation_context(context: ImplementationContext) -> ImplementationContext:
    remaining = IMPLEMENTATION_CONTEXT_MAX_CHARS

    def take(values: tuple[str, ...]) -> tuple[str, ...]:
        nonlocal remaining
        result: list[str] = []
        for value in values:
            if remaining <= 0:
                break
            if not value.strip():
                continue
            taken = value[:remaining]
            result.append(taken)
            remaining -= len(taken)
        return tuple(result)

    return ImplementationContext(
        requirements=take(context.requirements),
        constraints=take(context.constraints),
        files=take(context.files),
        acceptance=take(context.acceptance),
    )


def write(features: list[Feature]) -> None:
    """Overwrites the whole list — used by `plan` (session 0) and mark_passed."""
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        payload = {"items": [f.to_dict() for f in features]}
        write_text_atomic(_FILE_PATH, json.dumps(payload, indent=2))
    except Exception as ex:
        harness_log.error(f"[FeatureStore] failed to write: {ex}")


def parse(features_json: str) -> list[Feature]:
    """Interprets the raw feature array the driver returns from `plan`
    (`[{"id":1,"title":"...","priority":1}, ...]`). Forces `passes = False` (every feature
    is born pending) and reindexes missing/duplicate ids by order. Empty list if the JSON
    doesn't parse — the caller re-issues the request (corrective loop), it doesn't bring
    the run down.
    """
    try:
        parsed = json.loads(features_json)
        if not isinstance(parsed, list) or len(parsed) == 0:
            return []

        # Preserve explicit ids and assign missing ids collision-free. Duplicate explicit
        # ids are rejected because silently rewriting references makes the plan ambiguous.
        explicit = [Feature.from_dict(raw).id for raw in parsed if isinstance(raw, dict) and Feature.from_dict(raw).id > 0]
        if len(explicit) != len(set(explicit)):
            raise ValueError("duplicate explicit feature id")
        used = set(explicit)
        next_id = 1
        reindexed: list[Feature] = []
        for i, raw in enumerate(parsed):
            if not isinstance(raw, dict):
                raise TypeError("each feature must be a JSON object")
            candidate = Feature.from_dict(raw)
            if candidate.id > 0:
                fid = candidate.id
            else:
                while next_id in used:
                    next_id += 1
                fid = next_id
                used.add(fid)
                next_id += 1
            if not candidate.title.strip():
                raise ValueError("feature title cannot be blank")
            if candidate.priority <= 0:
                raise ValueError("feature priority must be positive")
            reindexed.append(replace(
                candidate,
                id=fid,
                passes=False,
                depends_on=tuple(dict.fromkeys(candidate.deps)),
                description=_truncate_description(candidate.description),
                references=tuple(dict.fromkeys(r for r in candidate.refs if r.strip())),
                implementation_context=_truncate_implementation_context(candidate.context),
            ))

        error = _dependency_graph_error(reindexed)
        if error is not None:
            harness_log.error(f"[FeatureStore] invalid dependency graph: {error}")
            return []

        return reindexed
    except Exception as ex:
        harness_log.error(f"[FeatureStore] failed to parse features: {ex}")
        return []


def _truncate_description(description: str) -> str:
    """Cuts at DESCRIPTION_MAX_CHARS chars — never raises, never rejects the whole
    feature over this, only shortens it."""
    return description[:DESCRIPTION_MAX_CHARS]


def _dependency_graph_error(features: list[Feature]) -> str | None:
    """`None` if the `deps` graph is valid (every id exists, no cycle); otherwise a
    description of the problem. Kahn's algorithm (topological sort): a node left outside
    the resolved set means a cycle. Dangling refs are checked first — otherwise a phantom
    dependency would be counted as eternally unresolved and reported as a "cycle" when
    it's actually an invalid id.
    """
    valid_ids = {f.id for f in features}

    dangling = [f"{f.id}->{dep}" for f in features for dep in f.deps if dep not in valid_ids]
    if dangling:
        return f"dependsOn references nonexistent id(s): {', '.join(dangling)}"

    # Tolerant group-by (duplicate ids aren't deduplicated by the reindex): the first id
    # seen sets the indegree, the same choice made on the .NET side.
    indegree: dict[int, int] = {}
    for f in features:
        if f.id not in indegree:
            indegree[f.id] = len(f.deps)

    dependents: dict[int, list[int]] = {}
    for f in features:
        for dep in f.deps:
            dependents.setdefault(dep, []).append(f.id)

    queue: deque[int] = deque(fid for fid, deg in indegree.items() if deg == 0)
    resolved: set[int] = set()
    while queue:
        fid = queue.popleft()
        if fid in resolved:
            continue
        resolved.add(fid)
        for dependent in dependents.get(fid, []):
            if dependent in indegree:
                indegree[dependent] -= 1
                if indegree[dependent] == 0:
                    queue.append(dependent)

    if len(resolved) == len(indegree):
        return None

    cyclic = [str(fid) for fid in indegree if fid not in resolved]
    return f"cyclic dependency among features: {', '.join(cyclic)}"


def load() -> list[Feature]:
    try:
        p = Path(_FILE_PATH)
        if not p.exists():
            return []

        payload = json.loads(p.read_text())
        items = payload.get("items") if isinstance(payload, dict) else None
        if not isinstance(items, list):
            return []
        return [Feature.from_dict(item) for item in items]
    except Exception as ex:
        harness_log.error(f"[FeatureStore] failed to load: {ex}")
        return []


def next_pending() -> Feature | None:
    """The next feature to implement: the highest priority (lowest `priority`) among the
    READY ones (every id in `deps` already has `passes == True`); ties broken by `id`.
    `None` when there's no ready pending item — this can mean actual completion (nothing
    pending) or blocked dependencies. Kahn's "ready set" recomputed on every call over the
    loaded list — no persisted graph structure.
    """
    features = load()
    passed = {f.id for f in features if f.passes}

    ready = [f for f in features if not f.passes and all(dep in passed for dep in f.deps)]
    if not ready:
        return None

    ready.sort(key=lambda f: (f.priority, f.id))
    return ready[0]


def mark_passed(id_: int) -> None:
    """Marks the feature as complete and rewrites the list. No-op if the id doesn't exist."""
    features = load()
    if not any(f.id == id_ for f in features):
        return

    write([replace(f, passes=True) if f.id == id_ else f for f in features])


def pending_count() -> int:
    """How many features are still pending (`passes == False`)."""
    return sum(1 for f in load() if not f.passes)


def all_passing() -> bool:
    """There are features and all of them passed — the loop's termination condition."""
    features = load()
    return len(features) > 0 and all(f.passes for f in features)


def reset() -> None:
    """Deletes the previous run's list — the PRODUCER flow resets it on its `start`. Also
    clears plan-revision and plan-observation evidence: a new run must not see a previous
    run's replan history. Imported lazily — plan_revision_store imports Feature/
    PlanRevision from this module at load time, so a module-level import here would cycle
    (same reasoning as state_keys.py's docstring, applied to stores instead of tasks/prompts)."""
    from harness_engine import plan_observation_store, plan_revision_store

    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        harness_log.error(f"[FeatureStore] failed to clear: {ex}")
    plan_revision_store.reset()
    plan_observation_store.reset()


# --- plan revisions (Replan) --------------------------------------------------------
#
# A revision is a driver-proposed, complete replacement of the active plan — evaluated by
# plan_revision_evaluator (soft, evidence/invariant judgement) and then applied here (hard
# domain invariants: passed features are immutable, the dependency graph must stay valid).


@dataclass(frozen=True)
class PlanRevision:
    """Driver-proposed replacement of the active plan, read from
    `.harness/replan.json` (plan_revision_store.PROPOSAL_PATH). Nullable fields mirror
    Feature's: an absent key from a hand-written or older proposal degrades to an empty
    tuple via the properties below, instead of raising."""

    reason: str
    alternatives_considered: tuple[str, ...] | None = None
    revised_features: tuple[Feature, ...] | None = None
    based_on_observation_ids: tuple[str, ...] | None = None

    @property
    def alternatives(self) -> tuple[str, ...]:
        return self.alternatives_considered if self.alternatives_considered is not None else ()

    @property
    def features(self) -> tuple[Feature, ...]:
        return self.revised_features if self.revised_features is not None else ()

    @property
    def observation_ids(self) -> tuple[str, ...]:
        return self.based_on_observation_ids if self.based_on_observation_ids is not None else ()

    def to_dict(self) -> dict[str, object]:
        return {
            "reason": self.reason,
            "alternativesConsidered": (
                list(self.alternatives_considered) if self.alternatives_considered is not None else None
            ),
            "revisedFeatures": (
                [f.to_dict() for f in self.revised_features] if self.revised_features is not None else None
            ),
            "basedOnObservationIds": (
                list(self.based_on_observation_ids) if self.based_on_observation_ids is not None else None
            ),
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "PlanRevision":
        alternatives_raw = payload.get("alternativesConsidered")
        features_raw = payload.get("revisedFeatures")
        observation_ids_raw = payload.get("basedOnObservationIds")
        return PlanRevision(
            reason=str(payload.get("reason") or ""),
            alternatives_considered=(
                tuple(str(x) for x in alternatives_raw) if isinstance(alternatives_raw, list) else None
            ),
            revised_features=(
                tuple(Feature.from_dict(x) for x in features_raw if isinstance(x, dict))
                if isinstance(features_raw, list)
                else None
            ),
            based_on_observation_ids=(
                tuple(str(x) for x in observation_ids_raw) if isinstance(observation_ids_raw, list) else None
            ),
        )


@dataclass(frozen=True)
class PlanRevisionResult:
    """Outcome of `apply_revision`: either the accepted, merged feature list, or a
    rejection reason — never both."""

    success: bool
    error: str
    features: tuple[Feature, ...] = ()

    @staticmethod
    def accepted(features: list[Feature]) -> "PlanRevisionResult":
        return PlanRevisionResult(True, "", tuple(features))

    @staticmethod
    def rejected(error: str) -> "PlanRevisionResult":
        return PlanRevisionResult(False, error, ())


def apply_revision(revision: PlanRevision, max_features: int) -> "PlanRevisionResult":
    """Applies a complete replacement proposed during a running development flow. Passed
    features are immutable evidence: a revision must retain them byte-for-byte at the
    domain level and they remain passed. Pending features may be reprioritized, split,
    added or removed, provided the resulting dependency graph is valid.

    This is the hard, domain-invariant gate — deliberately separate from
    plan_revision_evaluator.evaluate (the soft, evidence/observation gate): a caller could
    in principle call this without going through the evaluator, and it must still refuse
    an invalid plan on its own.
    """
    if not revision.reason.strip():
        return PlanRevisionResult.rejected("a revision reason is required")
    if len(revision.alternatives) < 2:
        return PlanRevisionResult.rejected("at least two considered alternatives are required")
    if len(revision.features) == 0:
        return PlanRevisionResult.rejected("the revised plan must contain features")
    if len(revision.features) > max_features:
        return PlanRevisionResult.rejected(f"the revised plan exceeds the {max_features}-feature limit")

    proposed = _normalize_revision_features(revision.features)
    if proposed is None:
        return PlanRevisionResult.rejected(
            "features must have unique positive ids, titles and positive priorities"
        )

    current = load()
    proposed_by_id = {f.id: f for f in proposed}
    for passed in (f for f in current if f.passes):
        retained = proposed_by_id.get(passed.id)
        if retained is None:
            return PlanRevisionResult.rejected(f"passed feature #{passed.id} cannot be removed")
        if not _same_definition(passed, retained):
            return PlanRevisionResult.rejected(f"passed feature #{passed.id} cannot be modified")

    passed_ids = {f.id for f in current if f.passes}
    merged = [replace(f, passes=f.id in passed_ids) for f in proposed]
    graph_error = _dependency_graph_error(merged)
    if graph_error is not None:
        return PlanRevisionResult.rejected(graph_error)

    write(merged)
    return PlanRevisionResult.accepted(merged)


def _normalize_revision_features(features: tuple[Feature, ...]) -> list[Feature] | None:
    """Same per-feature normalization as `parse` (truncate description/implementation
    context, dedupe depends_on/references, force passes=False) but WITHOUT reindexing
    missing ids — revision features must already carry explicit positive ids."""
    if any(f.id <= 0 or not f.title.strip() or f.priority <= 0 for f in features):
        return None
    if len({f.id for f in features}) != len(features):
        return None

    return [
        replace(
            f,
            passes=False,
            depends_on=tuple(dict.fromkeys(f.deps)),
            description=_truncate_description(f.description),
            references=tuple(dict.fromkeys(r for r in f.refs if r.strip())),
            implementation_context=_truncate_implementation_context(f.context),
        )
        for f in features
    ]


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
