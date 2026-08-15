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
        if existing.name not in EXPECTED_FILES:
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
        for filename in EXPECTED_FILES:
            atomic_io.write_text_atomic(
                str(DESTINATION / filename), (staging / filename).read_text()
            )

        manifest = {
            "ownedFiles": list(EXPECTED_FILES),
            "fileDigests": digests,
            "manifestDigest": _digest(
                "|".join(digests[name] for name in EXPECTED_FILES)
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
