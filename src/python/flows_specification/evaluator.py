"""Pure structural gates for the Specification flow.

The functions in this module do not read or write files. Each receives a proposal and the
parent context needed to validate freshness, then returns every stable violation code found
in that proposal. This mirrors ``SpecificationEvaluator.cs``.
"""

from __future__ import annotations

from collections import Counter, deque
import json

MAX_IDEA_UTF8_BYTES = 20_000
MAX_DESIGN_CONTENT_UTF8_BYTES = 1_000_000
READINESS_VERDICTS = ("READY", "FAIL:product", "FAIL:analysis", "FAIL:design")
APPROVAL_DECISIONS = ("approved", "revise")


def _result(violations: list[tuple[str, str]]) -> dict:
    return {
        "passed": not violations,
        "violations": [{"code": c, "message": m} for c, m in violations],
    }


def _duplicate_ids(values, code: str, kind: str) -> list[tuple[str, str]]:
    return [
        (code, f"duplicate {kind} id '{value}'")
        for value, count in Counter(values).items()
        if count > 1
    ]


def _utf8_size(value: dict) -> int:
    return len(
        json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    )


def idea(document: dict, source: str | None, source_digest: str | None) -> dict:
    violations: list[tuple[str, str]] = []
    if document.get("schema") != "iao/idea/v1":
        violations.append(("IDEA_SCHEMA_UNKNOWN", "expected schema 'iao/idea/v1'"))
    if not str(document.get("title", "")).strip():
        violations.append(("IDEA_TITLE_MISSING", "title is required"))
    if not str(document.get("problem", "")).strip():
        violations.append(("IDEA_PROBLEM_MISSING", "problem is required"))
    if not document.get("users"):
        violations.append(("IDEA_USERS_MISSING", "at least one user is required"))
    if not document.get("desiredOutcomes"):
        violations.append(
            ("IDEA_OUTCOMES_MISSING", "at least one desired outcome is required")
        )
    violations.extend(
        _duplicate_ids(
            [q.get("id", "") for q in document.get("openQuestions", [])],
            "IDEA_OPEN_QUESTION_DUPLICATE_ID",
            "open question",
        )
    )
    size = _utf8_size(document)
    if size > MAX_IDEA_UTF8_BYTES:
        violations.append(
            (
                "IDEA_TOO_LARGE",
                f"idea is {size} UTF-8 bytes, exceeds the {MAX_IDEA_UTF8_BYTES}-byte limit",
            )
        )
    if not str(source or "").strip():
        violations.append(("IDEA_SOURCE_MISSING", "source is required"))
    if not source_digest:
        violations.append(("IDEA_DIGEST_MISSING", "source digest is required"))
    return _result(violations)


def prd(document: dict, current_idea_digest: str) -> dict:
    violations: list[tuple[str, str]] = []
    if document.get("schema") != "iao/prd/v1":
        violations.append(("PRD_SCHEMA_UNKNOWN", "expected schema 'iao/prd/v1'"))
    if not current_idea_digest or document.get("ideaDigest") != current_idea_digest:
        violations.append(
            (
                "PRD_IDEA_DIGEST_STALE",
                f"prd.ideaDigest '{document.get('ideaDigest', '')}' does not match the current accepted idea digest '{current_idea_digest}'",
            )
        )
    for key, code, kind in (
        ("goals", "PRD_GOAL_ID_DUPLICATE", "goal"),
        ("successMetrics", "PRD_METRIC_ID_DUPLICATE", "metric"),
        ("risks", "PRD_RISK_ID_DUPLICATE", "risk"),
        ("decisions", "PRD_DECISION_ID_DUPLICATE", "decision"),
    ):
        violations.extend(
            _duplicate_ids(
                [item.get("id", "") for item in document.get(key, [])], code, kind
            )
        )
    for key, code, label in (
        ("goals", "PRD_GOALS_EMPTY", "goal"),
        ("nonGoals", "PRD_NON_GOALS_EMPTY", "non-goal"),
        ("scope", "PRD_SCOPE_EMPTY", "scope entry"),
        ("successMetrics", "PRD_METRICS_EMPTY", "success metric"),
    ):
        if not document.get(key):
            violations.append((code, f"at least one {label} is required"))
    for metric in document.get("successMetrics", []):
        measure = str(metric.get("measure", "")).strip()
        target = str(metric.get("target", "")).strip()
        if not measure or not target or measure.casefold() == target.casefold():
            violations.append(
                (
                    "PRD_METRIC_MEASURE_TARGET_NOT_SEPARATE",
                    f"metric '{metric.get('id', '')}' must have a measure and a target that are both present and distinct",
                )
            )
    blocking = [
        q.get("id", "") for q in document.get("openQuestions", []) if q.get("blocking")
    ]
    if blocking:
        violations.append(
            (
                "PRD_OPEN_QUESTION_BLOCKING",
                f"unresolved blocking open question(s): {', '.join(blocking)}",
            )
        )
    return _result(violations)


def srs(document: dict, current_prd_digest: str, accepted_goal_ids: list[str]) -> dict:
    violations: list[tuple[str, str]] = []
    requirements = document.get("functionalRequirements", []) + document.get(
        "qualityRequirements", []
    )
    requirement_ids = {item.get("id") for item in requirements}
    acceptance_ids = {item.get("id") for item in document.get("acceptanceCriteria", [])}
    if document.get("schema") != "iao/srs/v1":
        violations.append(("SRS_SCHEMA_UNKNOWN", "expected schema 'iao/srs/v1'"))
    if not current_prd_digest or document.get("prdDigest") != current_prd_digest:
        violations.append(
            (
                "SRS_PRD_DIGEST_STALE",
                f"srs.prdDigest '{document.get('prdDigest', '')}' does not match the current accepted PRD digest '{current_prd_digest}'",
            )
        )
    violations.extend(
        _duplicate_ids(
            [item.get("id", "") for item in requirements],
            "SRS_REQUIREMENT_ID_DUPLICATE",
            "requirement",
        )
    )
    covered_goals = {
        goal_id for item in requirements for goal_id in item.get("goalIds", [])
    }
    for goal_id in accepted_goal_ids:
        if goal_id not in covered_goals:
            violations.append(
                (
                    "SRS_GOAL_NOT_COVERED",
                    f"goal '{goal_id}' is not covered by any requirement",
                )
            )
    for requirement in requirements:
        requirement_id = requirement.get("id", "")
        if not str(requirement.get("statement") or "").strip():
            violations.append(
                (
                    "SRS_REQUIREMENT_STATEMENT_MISSING",
                    f"requirement '{requirement_id}' has no statement",
                )
            )
        if not requirement.get("acceptanceIds"):
            violations.append(
                (
                    "SRS_REQUIREMENT_WITHOUT_ACCEPTANCE",
                    f"requirement '{requirement_id}' has no acceptance criterion",
                )
            )
        for acceptance_id in requirement.get("acceptanceIds", []):
            if acceptance_id not in acceptance_ids:
                violations.append(
                    (
                        "SRS_ACCEPTANCE_REFERENCE_DANGLING",
                        f"requirement '{requirement_id}' references unknown acceptance criterion '{acceptance_id}'",
                    )
                )
        for dependency_id in requirement.get("dependsOn", []):
            if dependency_id not in requirement_ids:
                violations.append(
                    (
                        "SRS_REQUIREMENT_DEPENDENCY_DANGLING",
                        f"requirement '{requirement_id}' depends on unknown requirement '{dependency_id}'",
                    )
                )
    for criterion in document.get("acceptanceCriteria", []):
        if not all(
            str(criterion.get(field) or "").strip() for field in ("given", "when", "then")
        ):
            violations.append(
                (
                    "SRS_ACCEPTANCE_CRITERION_TEXT_MISSING",
                    f"acceptance criterion '{criterion.get('id', '')}' has an empty given/when/then",
                )
            )
        for requirement_id in criterion.get("requirementIds", []):
            if requirement_id not in requirement_ids:
                violations.append(
                    (
                        "SRS_ACCEPTANCE_CRITERION_REQUIREMENT_DANGLING",
                        f"acceptance criterion '{criterion.get('id', '')}' references unknown requirement '{requirement_id}'",
                    )
                )
    for item in document.get("interfaces", []):
        if not all(str(item.get(field) or "").strip() for field in ("name", "description")):
            violations.append(
                (
                    "SRS_INTERFACE_TEXT_MISSING",
                    f"interface '{item.get('id', '')}' has an empty name or description",
                )
            )
    for item in document.get("dataRules", []):
        if not str(item.get("rule") or "").strip():
            violations.append(
                (
                    "SRS_DATA_RULE_TEXT_MISSING",
                    f"data rule '{item.get('id', '')}' has no rule text",
                )
            )
    for key, code, label in (
        ("interfaces", "SRS_INTERFACE_REQUIREMENT_DANGLING", "interface"),
        ("dataRules", "SRS_DATA_RULE_REQUIREMENT_DANGLING", "data rule"),
    ):
        for item in document.get(key, []):
            for requirement_id in item.get("requirementIds", []):
                if requirement_id not in requirement_ids:
                    violations.append(
                        (
                            code,
                            f"{label} '{item.get('id', '')}' references unknown requirement '{requirement_id}'",
                        )
                    )
    delivery = document.get("delivery") or {}
    if not all(
        str(delivery.get(field) or "").strip() for field in ("target", "verificationStrategy")
    ):
        violations.append(
            (
                "SRS_DELIVERY_TEXT_MISSING",
                "delivery contract has an empty target or verification strategy",
            )
        )
    return _result(violations)


def sdd(
    document: dict,
    current_srs_digest: str,
    requirement_ids: list[str],
    current_source_digest: str | None = None,
    current_source_files: list[str] | None = None,
) -> dict:
    violations: list[tuple[str, str]] = []
    known_requirements = set(requirement_ids)
    allocated = {
        rid for adr in document.get("adrs", []) for rid in adr.get("requirementIds", [])
    }
    if document.get("schema") != "iao/sdd/v1":
        violations.append(("SDD_SCHEMA_UNKNOWN", "expected schema 'iao/sdd/v1'"))
    if not current_srs_digest or document.get("srsDigest") != current_srs_digest:
        violations.append(
            (
                "SDD_SRS_DIGEST_STALE",
                f"sdd.srsDigest '{document.get('srsDigest', '')}' does not match the current accepted SRS digest '{current_srs_digest}'",
            )
        )
    violations.extend(
        _duplicate_ids(
            [item.get("id", "") for item in document.get("adrs", [])],
            "SDD_ADR_ID_DUPLICATE",
            "ADR",
        )
    )
    for requirement_id in requirement_ids:
        if requirement_id not in allocated:
            violations.append(
                (
                    "SDD_REQUIREMENT_NOT_ALLOCATED",
                    f"requirement '{requirement_id}' is not allocated to any component/ADR",
                )
            )
    for adr in document.get("adrs", []):
        for requirement_id in adr.get("requirementIds", []):
            if requirement_id not in known_requirements:
                violations.append(
                    (
                        "SDD_ADR_REQUIREMENT_DANGLING",
                        f"ADR '{adr.get('id', '')}' references unknown requirement '{requirement_id}'",
                    )
                )
    for control in document.get("controls", []):
        for requirement_id in control.get("requirementIds", []):
            if requirement_id not in known_requirements:
                violations.append(
                    (
                        "SDD_CONTROL_REQUIREMENT_DANGLING",
                        f"control '{control.get('id', '')}' references unknown requirement '{requirement_id}'",
                    )
                )
    design_content = document.get("designContent")
    if str(design_content or "").strip() and len(str(design_content).encode("utf-8")) > MAX_DESIGN_CONTENT_UTF8_BYTES:
        violations.append(
            (
                "SDD_DESIGN_CONTENT_TOO_LARGE",
                f"designContent exceeds the {MAX_DESIGN_CONTENT_UTF8_BYTES}-byte UTF-8 limit",
            )
        )
    if str(current_source_digest or "").strip():
        if not str(document.get("sourceDigest") or "").strip():
            violations.append(
                (
                    "SDD_SOURCE_DIGEST_MISSING",
                    "sourceDigest is required when an accepted source bundle exists",
                )
            )
        elif document.get("sourceDigest") != current_source_digest:
            violations.append(
                (
                    "SDD_SOURCE_DIGEST_STALE",
                    f"sdd.sourceDigest '{document.get('sourceDigest', '')}' does not match the current accepted source digest '{current_source_digest}'",
                )
            )
        expected_files = current_source_files or []
        actual_files = document.get("sourceFiles")
        if not actual_files:
            violations.append(
                (
                    "SDD_SOURCE_FILES_MISSING",
                    "sourceFiles is required when an accepted source bundle exists",
                )
            )
        elif actual_files != expected_files:
            violations.append(
                (
                    "SDD_SOURCE_FILES_STALE",
                    "sdd.sourceFiles does not match the current accepted source bundle",
                )
            )
        if not str(design_content or "").strip():
            violations.append(
                (
                    "SDD_DESIGN_CONTENT_MISSING",
                    "designContent is required when an accepted source bundle exists",
                )
            )
    return _result(violations)


def _has_cycle(slices: list[dict]) -> bool:
    indegree = {item.get("id"): 0 for item in slices}
    adjacency = {item.get("id"): [] for item in slices}
    for item in slices:
        for dependency in item.get("dependsOn", []):
            if dependency != item.get("id") and dependency in adjacency:
                adjacency[dependency].append(item.get("id"))
                indegree[item.get("id")] += 1
    queue = deque(key for key, value in indegree.items() if value == 0)
    visited = 0
    while queue:
        current = queue.popleft()
        visited += 1
        for dependent in adjacency[current]:
            indegree[dependent] -= 1
            if indegree[dependent] == 0:
                queue.append(dependent)
    return visited < len(indegree)


def readiness(document: dict, requirement_ids: list[str], adr_ids: list[str]) -> dict:
    violations: list[tuple[str, str]] = []
    verdict = document.get("verdict", "")
    slices = document.get("slices") or []
    if verdict not in READINESS_VERDICTS:
        violations.append(
            (
                "READINESS_VERDICT_INVALID",
                f"verdict '{verdict}' is not one of: {', '.join(READINESS_VERDICTS)}",
            )
        )
    if "conflicts" not in document or document.get("conflicts") is None:
        violations.append(
            ("READINESS_CONFLICTS_MISSING", "conflicts must be a non-null array")
        )
    if "residuals" not in document or document.get("residuals") is None:
        violations.append(
            ("READINESS_RESIDUALS_MISSING", "residuals must be a non-null array")
        )
    if verdict != "READY":
        return _result(violations)
    if not slices:
        violations.append(
            (
                "READINESS_SLICES_EMPTY",
                "a READY verdict requires at least one readiness slice",
            )
        )
    if len(slices) > 10:
        violations.append(
            (
                "READINESS_TOO_MANY_SLICES",
                f"{len(slices)} slices exceeds the 10-slice cap",
            )
        )
    violations.extend(
        _duplicate_ids(
            [item.get("id", "") for item in slices],
            "READINESS_SLICE_ID_DUPLICATE",
            "readiness slice",
        )
    )
    known_requirements = set(requirement_ids)
    known_adrs = set(adr_ids)
    slice_ids = {item.get("id") for item in slices}
    for item in slices:
        for requirement_id in item.get("requirementIds", []):
            if requirement_id not in known_requirements:
                violations.append(
                    (
                        "READINESS_REQUIREMENT_REFERENCE_DANGLING",
                        f"slice '{item.get('id', '')}' references unknown requirement '{requirement_id}'",
                    )
                )
        for adr_id in item.get("adrIds", []):
            if adr_id not in known_adrs:
                violations.append(
                    (
                        "READINESS_ADR_REFERENCE_DANGLING",
                        f"slice '{item.get('id', '')}' references unknown ADR '{adr_id}'",
                    )
                )
        for dependency in item.get("dependsOn", []):
            if dependency == item.get("id") or dependency not in slice_ids:
                violations.append(
                    (
                        "READINESS_DEPENDENCY_REFERENCE_DANGLING",
                        f"slice '{item.get('id', '')}' depends on unknown or self-referencing slice '{dependency}'",
                    )
                )
    if _has_cycle(slices):
        violations.append(
            ("READINESS_DEPENDENCY_CYCLE", "the slice dependsOn graph contains a cycle")
        )
    if slices and not any(not item.get("dependsOn") for item in slices):
        violations.append(
            (
                "READINESS_NO_INITIAL_SLICE",
                "at least one slice must have an empty dependsOn",
            )
        )
    covered = {rid for item in slices for rid in item.get("requirementIds", [])}
    for requirement_id in requirement_ids:
        if requirement_id not in covered:
            violations.append(
                (
                    "READINESS_REQUIREMENT_NOT_SLICED",
                    f"requirement '{requirement_id}' is not covered by any readiness slice",
                )
            )
    return _result(violations)


def approval(document: dict, current_bundle_digest: str) -> dict:
    violations: list[tuple[str, str]] = []
    decision = document.get("decision", "")
    if decision not in APPROVAL_DECISIONS:
        violations.append(
            (
                "APPROVAL_DECISION_INVALID",
                f"decision '{decision}' is not one of: approved, revise",
            )
        )
    if (
        not current_bundle_digest
        or document.get("bundleDigest") != current_bundle_digest
    ):
        violations.append(
            (
                "APPROVAL_BUNDLE_DIGEST_STALE",
                f"decision.bundleDigest '{document.get('bundleDigest', '')}' does not match the current bundle digest '{current_bundle_digest}'",
            )
        )
    if not str(document.get("rationale", "")).strip():
        violations.append(("APPROVAL_RATIONALE_MISSING", "rationale is required"))
    return _result(violations)


def development_readiness(
    prd_document,
    srs_document,
    sdd_document,
    readiness_document,
    bundle_digest_at_approval,
    current_bundle_digest,
    rendered_documents,
    docs_max_chars,
):
    violations: list[tuple[str, str]] = []
    if not current_bundle_digest or bundle_digest_at_approval != current_bundle_digest:
        violations.append(
            ("DEV_READINESS_BUNDLE_DIGEST_STALE", "approval bundle digest is stale")
        )
    blocking = [
        q.get("id", "")
        for q in prd_document.get("openQuestions", [])
        if q.get("blocking")
    ]
    if blocking:
        violations.append(
            (
                "DEV_READINESS_BLOCKING_QUESTION_OPEN",
                f"unresolved blocking open question(s): {', '.join(blocking)}",
            )
        )
    requirements = [
        r.get("id")
        for r in srs_document.get("functionalRequirements", [])
        + srs_document.get("qualityRequirements", [])
    ]
    adrs = [a.get("id") for a in sdd_document.get("adrs", [])]
    readiness_result = readiness(readiness_document, requirements, adrs)
    violations.extend(
        (item["code"], item["message"]) for item in readiness_result["violations"]
    )
    total_bytes = sum(
        len(value.encode("utf-8")) for value in rendered_documents.values()
    )
    if total_bytes > docs_max_chars:
        violations.append(
            (
                "DEV_READINESS_BUNDLE_TOO_LARGE",
                f"rendered bundle is {total_bytes} UTF-8 bytes, exceeds the configured docsMaxChars ceiling of {docs_max_chars}",
            )
        )
    return _result(violations)
