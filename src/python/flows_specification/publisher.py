"""Secure publication of the accepted specification bundle."""

from __future__ import annotations

from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import tempfile

from harness_engine import atomic_io, docs_reader

from . import renderer, store

DESTINATION = Path("specs/active")
MANIFEST = store.DIR / "publish-manifest.json"
EXPECTED_FILES = (
    "00-prd.md",
    "10-software-requirements-specification.md",
    "20-software-design-document.md",
    "30-readiness-handoff.md",
)
DEVELOPMENT_PLAN_FILENAME = "40-development-plan.json"
EXPECTED_PUBLISHED_FILES = EXPECTED_FILES + (DEVELOPMENT_PLAN_FILENAME,)


def _digest(content: str) -> str:
    return "sha256:" + hashlib.sha256(content.encode("utf-8")).hexdigest()


def render_all(prd: dict, srs: dict, sdd: dict, readiness: dict) -> dict[str, str]:
    """Render the exact four documents that ``publish`` will write."""
    return {
        EXPECTED_FILES[0]: renderer.prd(prd),
        EXPECTED_FILES[1]: renderer.srs(srs),
        EXPECTED_FILES[2]: renderer.sdd(sdd),
        EXPECTED_FILES[3]: renderer.readiness(readiness),
    }


def build_development_plan(srs: dict, sdd: dict, readiness: dict, rendered: dict[str, str]) -> dict:
    requirements = {
        item.get("id"): item.get("statement", "")
        for item in (srs.get("functionalRequirements", []) + srs.get("qualityRequirements", []))
        if item.get("id")
    }
    adrs = {item.get("id"): item for item in sdd.get("adrs", []) if item.get("id")}
    slice_ids = {item.get("id"): index + 1 for index, item in enumerate(readiness.get("slices", []))}
    features = []
    for index, item in enumerate(readiness.get("slices", [])):
        requirement_ids = set(item.get("requirementIds", []))

        def linked(artifact: dict) -> bool:
            return bool(requirement_ids.intersection(artifact.get("requirementIds", [])))

        acceptance_criteria = [artifact for artifact in srs.get("acceptanceCriteria", []) if linked(artifact)]
        interfaces = [artifact for artifact in srs.get("interfaces", []) if linked(artifact)]
        data_rules = [artifact for artifact in srs.get("dataRules", []) if linked(artifact)]
        controls = [artifact for artifact in sdd.get("controls", []) if linked(artifact)]
        requirement_text = [f"{ref}: {requirements[ref]}" if ref in requirements else ref for ref in item.get("requirementIds", [])]
        decisions = []
        for ref in item.get("adrIds", []):
            adr = adrs.get(ref)
            decisions.append(
                f"{ref}: {adr.get('title', '')}. Decision: {adr.get('decision', '')}. Rationale: {adr.get('rationale', '')}"
                if adr else ref
            )
        references = list(dict.fromkeys(
            item.get("requirementIds", [])
            + item.get("adrIds", [])
            + [artifact.get("id") for artifact in acceptance_criteria]
            + [artifact.get("id") for artifact in interfaces]
            + [artifact.get("id") for artifact in data_rules]
            + [artifact.get("id") for artifact in controls]
        ))
        acceptance_text = [item.get("acceptanceCriterion", "")] + [
            f"{artifact.get('id', '')}: Given {artifact.get('given', '')}. "
            f"When {artifact.get('when', '')}. Then {artifact.get('then', '')}"
            for artifact in acceptance_criteria
        ]
        related_contract_text = [
            f"{artifact.get('id', '')}: {artifact.get('name', '')}. {artifact.get('description', '')}"
            for artifact in interfaces + controls
        ] + [
            f"{artifact.get('id', '')}: {artifact.get('rule', '')}"
            for artifact in data_rules
        ]
        features.append({
            "id": index + 1,
            "title": item.get("goal", ""),
            "priority": index + 1,
            "passes": False,
            "dependsOn": [slice_ids[ref] for ref in item.get("dependsOn", []) if ref in slice_ids],
            "description": (
                f"{item.get('goal', '')} Observable outcome: {item.get('observableOutcome', '')}. "
                f"Happy path: {item.get('happyPath', '')}. Failure path: {item.get('failurePath', '')}."
            ),
            "references": references,
            "implementationContext": {
                "requirements": requirement_text,
                "decisions": decisions,
                "constraints": [f"out of scope: {value}" for value in item.get("outOfScope", [])]
                + item.get("contracts", [])
                + related_contract_text,
                "files": [item.get("suggestedTarget", "")],
                "acceptance": acceptance_text,
            },
        })
    digest_source = "|".join(rendered[name] for name in EXPECTED_FILES)
    return {
        "schema": "iao/development-plan/v1",
        "specificationBundleDigest": _digest(digest_source),
        "sourceFiles": list(EXPECTED_FILES),
        "features": features,
        "targetDescription": (srs.get("delivery") or {}).get("target", ""),
        "verificationDescription": (srs.get("delivery") or {}).get("verificationStrategy", ""),
    }


def _read_manifest() -> dict | None:
    try:
        return json.loads(MANIFEST.read_text())
    except (OSError, json.JSONDecodeError):
        return None


def _ownership_error() -> str | None:
    manifest = _read_manifest() or {}
    owned = set(manifest.get("ownedFiles", []))
    if not DESTINATION.exists():
        return None
    for existing in DESTINATION.iterdir():
        if not existing.is_file():
            continue
        if existing.name not in EXPECTED_PUBLISHED_FILES:
            return f"unrecognized file '{existing.name}' exists in '{DESTINATION}'; publish blocked."
        if existing.name not in owned:
            return f"file '{existing.name}' exists in '{DESTINATION}' but is not owned by a previous publish; publish blocked."
    return None


def verify_postcondition(expected_digests: dict[str, str]) -> tuple[bool, str | None]:
    content, files = docs_reader.read(str(DESTINATION))
    if files != list(EXPECTED_FILES):
        return (
            False,
            f"postcondition failed: DocsReader returned files {files}, expected {list(EXPECTED_FILES)}.",
        )
    for filename in EXPECTED_FILES:
        path = DESTINATION / filename
        try:
            text = path.read_text()
        except OSError as error:
            return False, f"postcondition failed: could not read '{path}': {error}"
        actual = _digest(text)
        if expected_digests.get(filename) != actual:
            return (
                False,
                f"postcondition failed: '{filename}' digest does not match the staged digest.",
            )
        if text.rstrip() not in content:
            return (
                False,
                f"postcondition failed: DocsReader content does not contain '{filename}' in full.",
            )
    return True, None


def publish(
    prd: dict, srs: dict, sdd: dict, readiness: dict
) -> tuple[bool, str | None]:
    rendered = render_all(prd, srs, sdd, readiness)
    rendered[DEVELOPMENT_PLAN_FILENAME] = json.dumps(
        build_development_plan(srs, sdd, readiness, rendered), separators=(",", ":"), ensure_ascii=False
    )
    ownership_error = _ownership_error()
    if ownership_error:
        return False, ownership_error

    staging = Path(tempfile.mkdtemp(prefix="specification-", dir="."))
    try:
        digests: dict[str, str] = {}
        for filename, content in rendered.items():
            (staging / filename).write_text(content)
            digests[filename] = _digest(content)

        DESTINATION.mkdir(parents=True, exist_ok=True)
        for filename in EXPECTED_PUBLISHED_FILES:
            atomic_io.write_text_atomic(
                str(DESTINATION / filename), (staging / filename).read_text()
            )

        manifest = {
            "ownedFiles": list(EXPECTED_PUBLISHED_FILES),
            "fileDigests": digests,
            "manifestDigest": _digest(
                "|".join(digests[name] for name in EXPECTED_PUBLISHED_FILES)
            ),
            "publishedAt": datetime.now(timezone.utc).isoformat(),
        }
        store.DIR.mkdir(parents=True, exist_ok=True)
        atomic_io.write_text_atomic(
            str(MANIFEST), json.dumps(manifest, separators=(",", ":"))
        )
        return verify_postcondition(digests)
    finally:
        shutil.rmtree(staging, ignore_errors=True)
