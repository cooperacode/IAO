from __future__ import annotations
import hashlib
import json
import shutil
from pathlib import Path
from harness_engine import atomic_io

DIR = Path(".harness/specification/active")
PHASES = ("idea", "prd", "srs", "sdd", "review", "readiness", "approval")


def _path(name):
    return DIR / name


def canonical(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def digest(value):
    return "sha256:" + hashlib.sha256(canonical(value).encode()).hexdigest()


def load_run():
    try:
        return json.loads(_path("run.json").read_text())
    except Exception:
        return {
            "step": 0,
            "status": "in_progress",
            "phase": "start",
            "counters": {},
            "traceLabel": None,
            "terminalReason": None,
        }


def save_run(run):
    DIR.mkdir(parents=True, exist_ok=True)
    atomic_io.write_text_atomic(str(_path("run.json")), canonical(run))


def reset():
    if DIR.exists():
        shutil.rmtree(DIR)
    DIR.mkdir(parents=True, exist_ok=True)


def read_proposal(phase):
    try:
        return json.loads(_path(f"{phase}.proposal.json").read_text())
    except Exception:
        return None


def write_proposal(phase, value):
    DIR.mkdir(parents=True, exist_ok=True)
    atomic_io.write_text_atomic(str(_path(f"{phase}.proposal.json")), canonical(value))


def write_accepted(phase, value):
    DIR.mkdir(parents=True, exist_ok=True)
    atomic_io.write_text_atomic(str(_path(f"{phase}.accepted.json")), canonical(value))
    return digest(value)


def read_accepted(phase):
    try:
        value = json.loads(_path(f"{phase}.accepted.json").read_text())
        return value, digest(value)
    except Exception:
        return None, None


def bundle_digest():
    # Always joins exactly 5 segments in this fixed order (idea|prd|srs|sdd|readiness),
    # using "" for any phase not yet accepted — never omitting the position. An incomplete
    # chain still yields a stable (if unmatchable) digest instead of a shorter/misaligned
    # join. Mirrors SpecificationStore.BundleDigest() in the .NET port. Hashes the joined
    # string's raw UTF-8 bytes directly (not digest(), which JSON-encodes its argument
    # first) — same as SpecificationStore.Digest(string canonicalJson) in the .NET port.
    segments = [read_accepted(p)[1] or "" for p in ("idea", "prd", "srs", "sdd", "readiness")]
    joined = "|".join(segments)
    return "sha256:" + hashlib.sha256(joined.encode()).hexdigest()
