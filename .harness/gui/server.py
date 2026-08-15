#!/usr/bin/env python3
"""Harness GUI orchestrator: static dashboard + read API over the harness artifacts + control
API to start/stop a background CLI driver (Claude/Codex) that plays the "operational
interpreter" role a human normally follows manually (see .claude/agents/*.agent.md).

Stdlib-only, consistent with the rest of `.harness/` (no new dependency). Binds to loopback by
default — unlike the existing read-only `.harness/run.sh` monitor, this server has *write*
endpoints (start/stop a process with broad write/commit power), so it should not be exposed
beyond localhost without a deliberate choice to do so.
"""
from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, unquote, urlparse

import drivers
import process_manager
import sources_manager

REPO_ROOT = Path(__file__).resolve().parents[2]
GUI_DIR = Path(__file__).resolve().parent
HARNESS_DIR = REPO_ROOT / ".harness"
INDEX_HTML = GUI_DIR / "index.html"

# Mirrors the candidate-with-fallback map that used to live client-side in .harness/index.html —
# now resolved server-side so /api/files/<key> always returns one ready-to-render JSON shape.
FILE_SOURCES = {
    "state": ["state.json", "last-development.state.json", "last-run.state.json"],
    "features": ["feature_list.json"],
    "config": ["run_config.json"],
    "trace": ["trace.jsonl", "last-development.trace.jsonl", "last-run.trace.jsonl"],
    "inbox": ["inbox.json", "inbox.consumed.json"],
    "artifacts": ["artifacts.json"],
    "brief": ["brief.md"],
    "progress": ["../progress.txt"],
}

# Specification's own step-machine, distinct from the generic state.json/trace.jsonl above —
# see .harness/specification/active/run.json and run-specification.sh's phase list.
SPECIFICATION_DIR = HARNESS_DIR / "specification" / "active"
SPECIFICATION_RUN_JSON = SPECIFICATION_DIR / "run.json"
PUBLISHED_DOCUMENTS_DIR = REPO_ROOT / "specs" / "active"
SPECIFICATION_PHASES = [
    "start", "discover", "product", "analysis", "design", "review", "approve", "stop",
]
RUN_SPECIFICATION_SH = REPO_ROOT / "run-specification.sh"

# Fixed whitelist of the proposal/accepted document pairs the specification engine writes —
# never derived from request input, so there is no path-traversal surface here.
SPECIFICATION_DOCUMENTS = [
    ("sources", "sources.accepted.json", "Fontes · aceitas"),
    ("idea_proposal", "idea.proposal.json", "Ideia · proposta"),
    ("idea_accepted", "idea.accepted.json", "Ideia · aceita"),
    ("prd_proposal", "prd.proposal.json", "PRD · proposta"),
    ("prd_accepted", "prd.accepted.json", "PRD · aceito"),
    ("srs_proposal", "srs.proposal.json", "SRS · proposta"),
    ("srs_accepted", "srs.accepted.json", "SRS · aceita"),
    ("sdd_proposal", "sdd.proposal.json", "SDD · proposta"),
    ("sdd_accepted", "sdd.accepted.json", "SDD · aceito"),
    ("review_proposal", "review.proposal.json", "Review · proposta"),
    ("readiness_accepted", "readiness.accepted.json", "Readiness · aceito"),
    ("approval_proposal", "approval.proposal.json", "Aprovação · proposta"),
]
SPECIFICATION_REVISION_DIR = HARNESS_DIR / "specification" / "revisions"
SPECIFICATION_REVISION_CURRENT = SPECIFICATION_REVISION_DIR / "current.json"
SPECIFICATION_REVISION_REQUEST = SPECIFICATION_REVISION_DIR / "request.json"
SPECIFICATION_PHASE_ORDER = ["sources", "idea", "prd", "srs", "sdd", "review", "readiness", "approval"]
SPECIFICATION_ENGINE_PHASE = {
    "sources": "discover", "idea": "discover", "prd": "product", "srs": "analysis",
    "sdd": "design", "review": "review", "readiness": "review", "approval": "approve",
}
SPECIFICATION_PROPOSAL_TO_ACCEPTED = {
    "idea_proposal": "idea_accepted", "prd_proposal": "prd_accepted", "srs_proposal": "srs_accepted",
    "sdd_proposal": "sdd_accepted", "review_proposal": "readiness_accepted",
}
SPECIFICATION_DEPENDENTS = {
    "sources": ["idea", "prd", "srs", "sdd", "review", "readiness", "approval"],
    "idea": ["prd", "srs", "sdd", "review", "readiness", "approval"],
    "prd": ["srs", "sdd", "review", "readiness", "approval"],
    "srs": ["sdd", "review", "readiness", "approval"],
    "sdd": ["review", "readiness", "approval"],
    "review": ["readiness", "approval"],
    "readiness": ["approval"],
    "approval": [],
}
APPROVAL_DIGEST_RE = re.compile(r"current bundle digest(?: is)? ['\"]([^'\"]+)['\"]")
APPROVAL_PLACEHOLDER_DIGEST = "sha256:" + "0" * 64


def _resolve_candidate(key: str) -> tuple[str, Path] | None:
    candidates = FILE_SOURCES.get(key)
    if not candidates:
        return None
    repo_root_resolved = REPO_ROOT.resolve()
    fallback = candidates[0]
    for rel in candidates:
        candidate_path = (HARNESS_DIR / rel).resolve()
        try:
            candidate_path.relative_to(repo_root_resolved)
        except ValueError:
            continue  # escapes the repo root — never serve it
        if candidate_path.is_file():
            return rel, candidate_path
    return fallback, (HARNESS_DIR / fallback).resolve()


def _specification_document_path(key: str) -> Path:
    """Resolve a document key through the fixed whitelist used by the read API."""
    for document_key, filename, _label in SPECIFICATION_DOCUMENTS:
        if document_key == key:
            return SPECIFICATION_DIR / filename
    raise ValueError("documento de especificação inválido")


def _specification_phase(key: str) -> str:
    return "sources" if key == "sources" else key.removesuffix("_proposal").removesuffix("_accepted")


def _proposal_key(key: str) -> str:
    if key == "sources":
        return key
    if key == "readiness_accepted":
        return "review_proposal"
    if key == "approval_proposal":
        return key
    if key.endswith("_proposal"):
        return key
    return key.removesuffix("_accepted") + "_proposal"


def _accepted_key(key: str) -> str:
    if key == "sources":
        return key
    if key == "review_proposal":
        return "readiness_accepted"
    if key == "approval_proposal":
        return key
    if key.endswith("_accepted"):
        return key
    return key.removesuffix("_proposal") + "_accepted"


def _revision_state() -> dict:
    if not SPECIFICATION_REVISION_CURRENT.is_file():
        return {"active": False, "stalePhases": [], "revisionId": None}
    try:
        return json.loads(SPECIFICATION_REVISION_CURRENT.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return {"active": False, "stalePhases": [], "revisionId": None}


def _write_revision_state(state: dict) -> None:
    SPECIFICATION_REVISION_DIR.mkdir(parents=True, exist_ok=True)
    SPECIFICATION_REVISION_CURRENT.write_text(json.dumps(state, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def _revision_preview(key: str) -> dict:
    phase = _specification_phase(key)
    if phase not in SPECIFICATION_PHASE_ORDER:
        raise ValueError("fase de especificação inválida")
    stale = SPECIFICATION_DEPENDENTS[phase]
    return {
        "phase": phase,
        "proposalKey": _proposal_key(key),
        "acceptedKey": _accepted_key(key),
        "stalePhases": stale,
        "firstRegenerationPhase": stale[0] if stale else None,
        "requiresNewApproval": "approval" in stale or phase == "approval",
    }


def _save_revision_proposal(key: str, content) -> dict:
    if _specification_phase(key) == "sources":
        raise ValueError("fontes devem ser alteradas pela tela de Specs antes de uma nova descoberta")
    proposal_key = _proposal_key(key)
    path = _specification_document_path(proposal_key)
    if not isinstance(content, (dict, list)):
        raise ValueError("o conteúdo da proposta deve ser um objeto ou uma lista JSON")
    path.write_text(json.dumps(content, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    preview = _revision_preview(key)
    revision_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    state = {
        "active": True, "revisionId": revision_id, "phase": preview["phase"],
        "proposalKey": proposal_key, "acceptedKey": preview["acceptedKey"],
        "stalePhases": preview["stalePhases"], "status": "proposal_saved",
        "createdAt": datetime.now(timezone.utc).isoformat(),
    }
    _write_revision_state(state)
    return {"ok": True, "revision": state, "impact": preview}


def _apply_revision(key: str) -> dict:
    if _specification_phase(key) == "sources":
        raise ValueError("fontes não possuem uma proposta de revisão; altere-as pela tela de Specs")
    running = process_manager.status("specification").get("status")
    if running in ("running", "stopping"):
        raise ValueError("pare o driver de specification antes de aplicar uma revisão")
    proposal_key = _proposal_key(key)
    accepted_key = _accepted_key(key)
    proposal_path = _specification_document_path(proposal_key)
    accepted_path = _specification_document_path(accepted_key)
    if not proposal_path.is_file():
        raise FileNotFoundError("proposta de revisão não encontrada")
    if not accepted_path.is_file():
        raise FileNotFoundError("artefato aceito não encontrado")
    preview = _revision_preview(key)
    revision = _revision_state()
    revision_id = revision.get("revisionId") or datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    snapshot_dir = SPECIFICATION_REVISION_DIR / "snapshots" / revision_id
    snapshot_dir.mkdir(parents=True, exist_ok=True)
    affected_keys = [
        accepted_key,
        *[candidate for candidate, _filename, _label in SPECIFICATION_DOCUMENTS
          if _specification_phase(candidate) in preview["stalePhases"] and candidate.endswith("_accepted")],
    ]
    for candidate in affected_keys:
        source = _specification_document_path(candidate)
        if source.is_file():
            shutil.copy2(source, snapshot_dir / source.name)
    accepted_path.write_text(proposal_path.read_text(encoding="utf-8"), encoding="utf-8")
    revision.update({
        "active": True, "status": "applied", "appliedAt": datetime.now(timezone.utc).isoformat(),
        "proposalKey": proposal_key, "acceptedKey": accepted_key,
        "snapshot": str(snapshot_dir.relative_to(REPO_ROOT)), "stalePhases": preview["stalePhases"],
        "firstRegenerationPhase": preview["firstRegenerationPhase"],
    })
    run_phase = SPECIFICATION_ENGINE_PHASE[preview["phase"]]
    if SPECIFICATION_RUN_JSON.is_file():
        try:
            run = json.loads(SPECIFICATION_RUN_JSON.read_text(encoding="utf-8"))
            run.update({"phase": run_phase, "status": "in_progress", "terminalReason": None})
            SPECIFICATION_RUN_JSON.write_text(json.dumps(run, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            revision["enginePhase"] = run_phase
        except json.JSONDecodeError:
            revision["enginePhase"] = run_phase
    _write_revision_state(revision)
    return {"ok": True, "revision": revision, "impact": preview}


def _save_llm_revision_request(key: str, request: str) -> dict:
    accepted_targets = {"idea_accepted", "prd_accepted", "srs_accepted", "sdd_accepted", "readiness_accepted"}
    if key not in accepted_targets:
        raise ValueError("selecione um artefato aceito de ideia, PRD, SRS, SDD ou readiness")
    if not isinstance(request, str) or not request.strip():
        raise ValueError("descreva a alteração que deve ser feita")
    if process_manager.status("specification").get("status") in ("running", "stopping"):
        raise ValueError("pare o driver de specification antes de enviar uma nova solicitação")
    phase = _specification_phase(key)
    engine_phase = SPECIFICATION_ENGINE_PHASE[phase]
    if SPECIFICATION_RUN_JSON.is_file():
        try:
            current_run = json.loads(SPECIFICATION_RUN_JSON.read_text(encoding="utf-8"))
            if current_run.get("status") == "awaiting_approval":
                raise ValueError("conclua a aprovação da revisão atual antes de iniciar outra alteração")
        except json.JSONDecodeError:
            pass
    payload = {
        "schema": "iao/specification-revision-request/v1", "status": "pending",
        "targetKey": key, "targetFile": _specification_document_path(key).name,
        "enginePhase": engine_phase, "request": request.strip(),
        "createdAt": datetime.now(timezone.utc).isoformat(),
    }
    SPECIFICATION_REVISION_DIR.mkdir(parents=True, exist_ok=True)
    stale_approval = SPECIFICATION_DIR / "approval.proposal.json"
    if stale_approval.is_file():
        archive_dir = SPECIFICATION_REVISION_DIR / "stale-approval" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        archive_dir.mkdir(parents=True, exist_ok=True)
        shutil.move(str(stale_approval), str(archive_dir / stale_approval.name))
    SPECIFICATION_REVISION_REQUEST.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if SPECIFICATION_RUN_JSON.is_file():
        try:
            run = json.loads(SPECIFICATION_RUN_JSON.read_text(encoding="utf-8"))
            run.update({"phase": engine_phase, "status": "in_progress", "terminalReason": None})
            SPECIFICATION_RUN_JSON.write_text(json.dumps(run, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        except (OSError, json.JSONDecodeError):
            pass
    generic_state_path = HARNESS_DIR / "state.json"
    if generic_state_path.is_file():
        try:
            generic_state = json.loads(generic_state_path.read_text(encoding="utf-8"))
            shutil.copy2(generic_state_path, HARNESS_DIR / "last-revision.state.json")
            generic_state.update({"step": 0, "costChars": 0, "terminalReason": None})
            generic_state_path.write_text(json.dumps(generic_state, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        except (OSError, json.JSONDecodeError):
            pass
    return {"ok": True, "request": payload, "message": f"Solicitação preparada; o fluxo será retomado em {engine_phase}."}


def _fail_llm_revision_request(error: str) -> None:
    if not SPECIFICATION_REVISION_REQUEST.is_file():
        return
    try:
        payload = json.loads(SPECIFICATION_REVISION_REQUEST.read_text(encoding="utf-8"))
        if payload.get("status") in ("pending", "in_progress"):
            payload.update({"status": "failed", "error": error, "completedAt": datetime.now(timezone.utc).isoformat()})
            SPECIFICATION_REVISION_REQUEST.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    except (OSError, json.JSONDecodeError):
        pass


def _complete_llm_revision_request() -> None:
    if not SPECIFICATION_REVISION_REQUEST.is_file():
        return
    try:
        payload = json.loads(SPECIFICATION_REVISION_REQUEST.read_text(encoding="utf-8"))
        if payload.get("status") in ("pending", "in_progress", "awaiting_approval"):
            payload.update({"status": "completed", "completedAt": datetime.now(timezone.utc).isoformat()})
            SPECIFICATION_REVISION_REQUEST.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    except (OSError, json.JSONDecodeError):
        pass


def _save_specification_document(key: str, content) -> dict:
    path = _specification_document_path(key)
    if not path.is_file():
        raise FileNotFoundError("documento de especificação não encontrado")
    if not isinstance(content, (dict, list)):
        raise ValueError("o conteúdo do documento deve ser um objeto ou uma lista JSON")
    path.write_text(json.dumps(content, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"ok": True, "key": key, "modifiedAt": int(path.stat().st_mtime * 1000)}


class Handler(BaseHTTPRequestHandler):
    server_version = "HarnessGUI/1.0"

    def log_message(self, fmt: str, *args) -> None:  # quieter, timestamped one-liners
        sys.stderr.write(f"[harness-gui] {self.address_string()} - {fmt % args}\n")

    # -- response helpers ----------------------------------------------------------------
    def _json(self, status: int, payload) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _html(self, status: int, body_text: str) -> None:
        body = body_text.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_json_body(self) -> dict:
        length = int(self.headers.get("Content-Length", 0) or 0)
        if length == 0:
            return {}
        raw = self.rfile.read(length)
        try:
            return json.loads(raw.decode("utf-8"))
        except json.JSONDecodeError as exc:
            raise ValueError(f"invalid JSON body: {exc}") from exc

    # -- routing ---------------------------------------------------------------------------
    def do_GET(self) -> None:  # noqa: N802 (stdlib naming convention)
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)
        try:
            if path in ("/", "/index.html"):
                return self._serve_index()
            if path == "/favicon.ico":
                self.send_response(204)
                self.end_headers()
                return
            if path == "/api/flows":
                return self._json(200, process_manager.flow_availability())
            if path == "/api/drivers":
                payload = {
                    name: {**meta, "binaryAvailable": drivers.binary_available(name), "models": drivers.model_options(name)}
                    for name, meta in drivers.DRIVERS.items()
                }
                return self._json(200, payload)
            if path.startswith("/api/files/"):
                key = unquote(path[len("/api/files/"):])
                return self._serve_file_source(key)
            if path == "/api/process":
                flow = (query.get("flow") or [""])[0]
                if flow not in ("development", "specification"):
                    return self._json(400, {"error": "flow must be 'development' or 'specification'"})
                return self._json(200, process_manager.status(flow))
            if path == "/api/process/logs":
                flow = (query.get("flow") or [""])[0]
                since = int((query.get("since") or ["0"])[0] or 0)
                if flow not in ("development", "specification"):
                    return self._json(400, {"error": "flow must be 'development' or 'specification'"})
                return self._json(200, process_manager.read_logs(flow, since))
            if path == "/api/sources":
                return self._json(200, sources_manager.list_files())
            if path == "/api/specification/pipeline":
                return self._serve_specification_pipeline()
            if path == "/api/specification/documents":
                return self._serve_specification_documents()
            if path == "/api/published":
                return self._serve_published_documents()
            if path == "/api/specification/revision":
                return self._json(200, _revision_state())
            return self._json(404, {"error": "not found"})
        except Exception as exc:  # last-resort guard so a bug never hangs a client
            return self._json(500, {"error": str(exc)})

    def do_POST(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        path = parsed.path
        try:
            body = self._read_json_body()
            if path == "/api/process/start":
                flow = body.get("flow")
                driver = body.get("driver")
                model = body.get("model", "")
                if flow not in ("development", "specification"):
                    return self._json(400, {"error": "flow must be 'development' or 'specification'"})
                if driver not in drivers.DRIVERS:
                    return self._json(400, {"error": f"unknown driver '{driver}'"})
                if not isinstance(model, str):
                    return self._json(400, {"error": "model must be a string"})
                try:
                    data = process_manager.start(flow, driver, model)
                except (process_manager.ProcessConflict, process_manager.FlowUnavailable) as exc:
                    if flow == "specification":
                        _fail_llm_revision_request(str(exc))
                    raise
                return self._json(202, data)
            if path == "/api/process/stop":
                flow = body.get("flow")
                if flow not in ("development", "specification"):
                    return self._json(400, {"error": "flow must be 'development' or 'specification'"})
                data = process_manager.stop(flow)
                return self._json(202, data)
            if path == "/api/sources":
                name = body.get("filename")
                content = body.get("content", "")
                if not isinstance(name, str) or not isinstance(content, str):
                    return self._json(400, {"error": "expected {filename: string, content: string}"})
                saved = sources_manager.save_file(name, content)
                return self._json(200, saved)
            if path == "/api/specification/approve":
                return self._handle_specification_approve()
            if path == "/api/specification/revision/save":
                key = body.get("key")
                if not isinstance(key, str) or "content" not in body:
                    return self._json(400, {"error": "expected {key: string, content: object|array}"})
                return self._json(200, _save_revision_proposal(key, body["content"]))
            if path == "/api/specification/revision/apply":
                key = body.get("key")
                if not isinstance(key, str):
                    return self._json(400, {"error": "expected {key: string}"})
                return self._json(200, _apply_revision(key))
            if path == "/api/specification/revision/request":
                key = body.get("key")
                request = body.get("request")
                return self._json(200, _save_llm_revision_request(key, request))
            return self._json(404, {"error": "not found"})
        except process_manager.ProcessConflict as exc:
            return self._json(409, {"error": str(exc)})
        except process_manager.FlowUnavailable as exc:
            return self._json(400, {"error": str(exc)})
        except process_manager.ProcessNotFound as exc:
            return self._json(404, {"error": str(exc)})
        except ValueError as exc:
            return self._json(400, {"error": str(exc)})
        except Exception as exc:
            return self._json(500, {"error": str(exc)})

    def do_DELETE(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        path = parsed.path
        try:
            if path.startswith("/api/sources/"):
                name = unquote(path[len("/api/sources/"):])
                sources_manager.delete_file(name)
                return self._json(200, {"ok": True})
            return self._json(404, {"error": "not found"})
        except FileNotFoundError:
            return self._json(404, {"error": "file not found"})
        except ValueError as exc:
            return self._json(400, {"error": str(exc)})
        except Exception as exc:
            return self._json(500, {"error": str(exc)})

    # -- handlers ---------------------------------------------------------------------------
    def _serve_index(self) -> None:
        if not INDEX_HTML.is_file():
            return self._json(500, {"error": "gui/index.html is missing"})
        self._html(200, INDEX_HTML.read_text(encoding="utf-8"))

    def _serve_file_source(self, key: str) -> None:
        resolved = _resolve_candidate(key)
        if resolved is None:
            return self._json(404, {"error": f"unknown source key '{key}'"})
        rel, candidate_path = resolved
        exists = candidate_path.is_file()
        text = candidate_path.read_text(encoding="utf-8", errors="replace") if exists else ""
        self._json(200, {"path": rel, "exists": exists, "text": text})

    def _serve_specification_pipeline(self) -> None:
        payload = {
            "exists": False,
            "phase": None,
            "status": None,
            "step": None,
            "terminalReason": None,
            "phases": SPECIFICATION_PHASES,
        }
        if SPECIFICATION_RUN_JSON.is_file():
            try:
                data = json.loads(SPECIFICATION_RUN_JSON.read_text(encoding="utf-8"))
                payload.update(
                    exists=True,
                    phase=data.get("phase"),
                    status=data.get("status"),
                    step=data.get("step"),
                    terminalReason=data.get("terminalReason"),
                )
            except json.JSONDecodeError:
                pass  # leave exists=False — a half-written run.json shouldn't crash the panel
        # SPECIFICATION_RUN_JSON is the domain-level run tracker (phase/status as
        # SpecificationTasks understands it) — it is NOT where the engine records a fault. An
        # unhandled exception mid-turn (TaskRegistry.RunWithTimeout catching it) is written to
        # the shared, generic `.harness/state.json` (Harness.Engine.StateStore, the same file
        # Development's own dashboard reads), and *that* terminalReason is what makes
        # TaskRegistry.Dispatch refuse every subsequent turn — including a direct `approve` sent
        # from this GUI. Without this, the pipeline panel could show "awaiting_approval" forever
        # while the engine was actually already refusing to move, with no visible explanation.
        if not payload["terminalReason"]:
            payload["terminalReason"] = self._engine_terminal_reason()
        self._json(200, payload)

    @staticmethod
    def _engine_terminal_reason() -> str | None:
        state_path = HARNESS_DIR / "state.json"
        if not state_path.is_file():
            return None
        try:
            data = json.loads(state_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            return None
        reason = data.get("terminalReason")
        return reason if isinstance(reason, str) and reason else None

    def _serve_specification_documents(self) -> None:
        documents = []
        revision = _revision_state()
        revision_request = None
        if SPECIFICATION_REVISION_REQUEST.is_file():
            try:
                revision_request = json.loads(SPECIFICATION_REVISION_REQUEST.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                revision_request = None
        stale_phases = set(revision.get("stalePhases") or [])
        approval_pending = False
        if SPECIFICATION_RUN_JSON.is_file():
            try:
                approval_pending = json.loads(SPECIFICATION_RUN_JSON.read_text(encoding="utf-8")).get("status") == "awaiting_approval"
            except (OSError, json.JSONDecodeError):
                pass
        for key, filename, label in SPECIFICATION_DOCUMENTS:
            path = SPECIFICATION_DIR / filename
            exists = path.is_file()
            content = None
            proposal_content = None
            modified_at = None
            if exists:
                try:
                    content = json.loads(path.read_text(encoding="utf-8"))
                    modified_at = int(path.stat().st_mtime * 1000)
                except json.JSONDecodeError:
                    exists = False
            if key.endswith("_accepted"):
                proposal_path = _specification_document_path(_proposal_key(key))
                if proposal_path.is_file():
                    try:
                        proposal_content = json.loads(proposal_path.read_text(encoding="utf-8"))
                    except json.JSONDecodeError:
                        proposal_content = None
            documents.append({
                "key": key,
                "filename": filename,
                "label": label,
                "exists": exists,
                "modifiedAt": modified_at,
                "content": content,
                "proposalContent": proposal_content,
                "isCurrentRevision": revision.get("active") and key in (revision.get("proposalKey"), revision.get("acceptedKey")),
                "isStale": _specification_phase(key) in stale_phases,
                "proposalExists": bool(key.endswith("_accepted") and _specification_document_path(_proposal_key(key)).is_file()),
            })
        self._json(200, {
            "documents": documents,
            "revisionLocked": approval_pending or (revision_request is not None and revision_request.get("status") in ("pending", "in_progress", "awaiting_approval")),
            "revisionRequest": revision_request,
        })

    def _serve_published_documents(self) -> None:
        files = []
        if PUBLISHED_DOCUMENTS_DIR.is_dir():
            for path in sorted(PUBLISHED_DOCUMENTS_DIR.rglob("*")):
                if not path.is_file():
                    continue
                try:
                    relative = path.relative_to(PUBLISHED_DOCUMENTS_DIR)
                    stat = path.stat()
                    content = path.read_text(encoding="utf-8")
                except (OSError, UnicodeDecodeError):
                    continue
                files.append({
                    "name": path.name,
                    "path": relative.as_posix(),
                    "sizeBytes": stat.st_size,
                    "modifiedAt": int(stat.st_mtime * 1000),
                    "content": content,
                })
        self._json(200, {"directory": "specs/active", "exists": PUBLISHED_DOCUMENTS_DIR.is_dir(), "files": files})

    def _handle_specification_approve(self) -> None:
        status = process_manager.status("specification")
        if status.get("status") in ("running", "stopping"):
            return self._json(409, {
                "error": "Um driver de specification já está rodando em background — pare-o antes de aprovar diretamente pela GUI.",
            })
        if not RUN_SPECIFICATION_SH.is_file():
            return self._json(500, {"error": f"{RUN_SPECIFICATION_SH.name} não encontrado no repositório"})
        envelope = {"type": "command", "value": "approve", "args": [], "context": {"driver": "gui"}}

        # A READY review pauses before an approval proposal exists. The GUI is the
        # human approval surface, so bootstrap the proposal here instead of making
        # the operator hand-edit a JSON file. The engine reports the current bundle
        # digest when given a deliberately stale probe; that digest is then written
        # into the real proposal and the normal approval validation runs unchanged.
        approval_path = SPECIFICATION_DIR / "approval.proposal.json"
        if not approval_path.is_file():
            try:
                probe = {"decision": "approved", "bundleDigest": APPROVAL_PLACEHOLDER_DIGEST,
                         "approvedBy": "GUI", "decidedAt": "2026-01-01T00:00:00Z",
                         "rationale": "Aprovação confirmada pelo operador na GUI."}
                approval_path.write_text(json.dumps(probe, ensure_ascii=False) + "\n", encoding="utf-8")
                probe_result = subprocess.run(
                    [str(RUN_SPECIFICATION_SH), json.dumps(envelope)],
                    cwd=str(REPO_ROOT), capture_output=True, text=True, timeout=120,
                )
                match = APPROVAL_DIGEST_RE.search(probe_result.stdout + "\n" + probe_result.stderr)
                if not match:
                    approval_path.unlink(missing_ok=True)
                    return self._json(500, {"error": "o engine não informou o digest atual do bundle", "stdout": probe_result.stdout, "stderr": probe_result.stderr})
                probe["bundleDigest"] = match.group(1)
                approval_path.write_text(json.dumps(probe, ensure_ascii=False) + "\n", encoding="utf-8")
            except (OSError, subprocess.TimeoutExpired) as exc:
                approval_path.unlink(missing_ok=True)
                return self._json(500, {"error": f"não foi possível preparar a aprovação: {exc}"})

        try:
            result = subprocess.run(
                [str(RUN_SPECIFICATION_SH), json.dumps(envelope)],
                cwd=str(REPO_ROOT),
                capture_output=True,
                text=True,
                timeout=120,
            )
        except subprocess.TimeoutExpired as exc:
            return self._json(500, {"error": f"run-specification.sh não respondeu em tempo: {exc}"})
        except OSError as exc:
            return self._json(500, {"error": f"falha ao executar run-specification.sh: {exc}"})

        run_json = None
        if SPECIFICATION_RUN_JSON.is_file():
            try:
                run_json = json.loads(SPECIFICATION_RUN_JSON.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                run_json = None
        self._json(200, {
            "exitCode": result.returncode,
            "stdout": result.stdout,
            "stderr": result.stderr,
            "runJson": run_json,
        })
        if result.returncode == 0 and (not run_json or run_json.get("status") != "awaiting_approval"):
            _complete_llm_revision_request()


def _orphan_sweep_loop(stop_event: threading.Event, interval_seconds: float = 30.0) -> None:
    while not stop_event.wait(interval_seconds):
        process_manager.sweep_orphans()


def main() -> None:
    import os

    host = os.environ.get("HARNESS_GUI_HOST", "127.0.0.1")
    port = int(os.environ.get("HARNESS_GUI_PORT", "8787"))

    process_manager.sweep_orphans()
    stop_event = threading.Event()
    sweeper = threading.Thread(target=_orphan_sweep_loop, args=(stop_event,), daemon=True)
    sweeper.start()

    server = ThreadingHTTPServer((host, port), Handler)
    print(f"[harness-gui] serving on http://{host}:{port} (repo root: {REPO_ROOT})")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        stop_event.set()
        server.server_close()


if __name__ == "__main__":
    main()
