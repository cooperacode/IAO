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
    """Deletes the previous run's list — the PRODUCER flow resets it on its `start`."""
    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        harness_log.error(f"[FeatureStore] failed to clear: {ex}")
