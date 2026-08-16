"""Background process lifecycle for the harness GUI orchestrator.

One process at a time per flow ("development" | "specification"). State is persisted to
`.harness/gui/state/<flow>.process.json` so a server restart can rediscover (or correctly mark
as crashed) a process that is still running in the background. All child processes are started
in their own session (`start_new_session=True`) so `stop()` can terminate the whole process
group, not just the immediate PID Popen returned.
"""
from __future__ import annotations

import json
import os
import signal
import subprocess
import threading
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import drivers

REPO_ROOT = Path(__file__).resolve().parents[2]
STATE_DIR = Path(__file__).resolve().parent / "state"

_locks: dict[str, threading.Lock] = {}
_locks_guard = threading.Lock()


class ProcessConflict(Exception):
    """Raised when a start is requested for a flow that already has a live process."""


class ProcessNotFound(Exception):
    """Raised when stop/logs is requested for a flow with no known (or already dead) process."""


class FlowUnavailable(Exception):
    """Raised when the requested flow's wrapper script does not exist/is not executable."""


def _lock_for(flow: str) -> threading.Lock:
    with _locks_guard:
        if flow not in _locks:
            _locks[flow] = threading.Lock()
        return _locks[flow]


def _now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def _process_path(flow: str) -> Path:
    return STATE_DIR / f"{flow}.process.json"


def is_alive(pid: int | None) -> bool:
    if not pid:
        return False
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def load(flow: str) -> dict[str, Any] | None:
    path = _process_path(flow)
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return None


def save(flow: str, data: dict[str, Any]) -> None:
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    path = _process_path(flow)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, indent=2), encoding="utf-8")
    tmp.replace(path)


def _specification_approval_gate_active() -> bool:
    """Return whether Specification is waiting for the GUI's human approval.

    The headless driver is intentionally terminated at this point by
    ``_enforce_specification_approval_gate``.  The wrapper commonly reports that
    SIGTERM as exit code 143, which is a controlled stop rather than a failure.
    """
    run_path = REPO_ROOT / ".harness" / "specification" / "active" / "run.json"
    try:
        run = json.loads(run_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return False
    return run.get("status") == "awaiting_approval" and run.get("phase") == "approve"


def flow_availability() -> dict[str, dict[str, Any]]:
    def check(script_name: str) -> dict[str, Any]:
        candidates = [REPO_ROOT / script_name]
        for candidate in candidates:
            if candidate.is_file() and os.access(candidate, os.X_OK):
                return {"available": True, "wrapper": str(candidate.relative_to(REPO_ROOT))}
        return {"available": False, "wrapper": script_name}

    return {
        "development": check("run-development.sh"),
        "specification": check("run-specification.sh"),
    }


def status(flow: str) -> dict[str, Any]:
    """Returns the persisted status, reconciling it against the real PID when needed.

    Two cases self-heal here instead of leaving a stale label for the GUI to display:
    - "running" but the PID is gone (server was restarted, or the process crashed without
      anyone calling stop()) -> "crashed".
    - "stopping" but the PID is already gone -> "stopped". This closes a benign race with the
      `_watch` thread, which is the one that normally records the definitive exit code; if it
      hasn't written yet by the time a status/log poll comes in, we still want the UI to read
      something better than a status frozen at "stopping" forever.
    """
    data = load(flow)
    if not data:
        return {"flow": flow, "status": "idle"}
    if data.get("status") in ("running", "stopping") and not is_alive(data.get("pid")):
        # `_watch` may have just received the child exit and is about to persist
        # exitCode/status. Give it a short chance before treating a dead PID as a
        # crash; otherwise a normal harness `stop` is briefly shown as unexpected.
        if data.get("status") == "running":
            time.sleep(0.1)
        with _lock_for(flow):
            data = load(flow)  # re-read under the lock in case _watch just wrote the real outcome
            if data and data.get("status") in ("running", "stopping") and not is_alive(data.get("pid")):
                data["status"] = "crashed" if data["status"] == "running" else "stopped"
                data["endedAt"] = data.get("endedAt") or _now_iso()
                save(flow, data)
    return data or {"flow": flow, "status": "idle"}


def sweep_orphans() -> None:
    """Run once at server startup: reconcile persisted "running" state against real PIDs."""
    if not STATE_DIR.exists():
        return
    for path in STATE_DIR.glob("*.process.json"):
        flow = path.name[: -len(".process.json")]
        status(flow)


def _watch(flow: str, run_id: str, proc: subprocess.Popen) -> None:
    exit_code = proc.wait()
    with _lock_for(flow):
        data = load(flow)
        if not data or data.get("runId") != run_id:
            return  # a newer run has already replaced this one
        data["exitCode"] = exit_code
        data["endedAt"] = _now_iso()
        approval_gate_stop = (
            flow == "specification"
            and exit_code in (143, -signal.SIGTERM)
            and _specification_approval_gate_active()
        )
        data["status"] = "stopped" if approval_gate_stop or not exit_code else "error"
        save(flow, data)
        if flow == "specification":
            request_path = REPO_ROOT / ".harness" / "specification" / "revisions" / "request.json"
            if request_path.is_file():
                try:
                    request = json.loads(request_path.read_text(encoding="utf-8"))
                    if request.get("status") in ("pending", "in_progress"):
                        request_status = "failed" if exit_code else "completed"
                        run_path = REPO_ROOT / ".harness" / "specification" / "active" / "run.json"
                        try:
                            run = json.loads(run_path.read_text(encoding="utf-8"))
                            if run.get("status") == "awaiting_approval":
                                request_status = "awaiting_approval"
                        except (OSError, json.JSONDecodeError):
                            pass
                        request["status"] = request_status
                        request["completedAt"] = _now_iso()
                        request_path.write_text(json.dumps(request, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
                except (OSError, json.JSONDecodeError):
                    pass


def _enforce_specification_approval_gate(flow: str, proc: subprocess.Popen) -> None:
    """Stop a headless Specification driver as soon as human approval is required."""
    if flow != "specification":
        return
    run_path = REPO_ROOT / ".harness" / "specification" / "active" / "run.json"
    while proc.poll() is None:
        try:
            if run_path.is_file():
                run = json.loads(run_path.read_text(encoding="utf-8"))
                if run.get("status") == "awaiting_approval" and run.get("phase") == "approve":
                    try:
                        os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
                    except (ProcessLookupError, PermissionError):
                        pass
                    return
        except (OSError, json.JSONDecodeError):
            pass
        time.sleep(0.1)


def start(flow: str, driver: str, model: str | None = None) -> dict[str, Any]:
    availability = flow_availability().get(flow)
    if not availability or not availability["available"]:
        raise FlowUnavailable(f"flow '{flow}' has no executable wrapper at repo root")

    with _lock_for(flow):
        current = load(flow)
        if current and is_alive(current.get("pid")):
            raise ProcessConflict(f"flow '{flow}' already has a running process (pid {current['pid']})")

        run_id = uuid.uuid4().hex
        command = drivers.build_command(driver, flow, model)
        STATE_DIR.mkdir(parents=True, exist_ok=True)
        log_path = STATE_DIR / f"{flow}-{run_id}.driver.jsonl"

        log_fh = open(log_path, "wb")
        try:
            proc = subprocess.Popen(
                command,
                cwd=str(REPO_ROOT),
                stdout=log_fh,
                stderr=subprocess.STDOUT,
                stdin=subprocess.DEVNULL,
                start_new_session=True,
            )
        finally:
            log_fh.close()

        data = {
            "schema": "iao.gui.process.v1",
            "flow": flow,
            "driver": driver,
            "model": model or "",
            "runId": run_id,
            "pid": proc.pid,
            "status": "running",
            "startedAt": _now_iso(),
            "endedAt": None,
            "exitCode": None,
            "command": command,
            "cwd": str(REPO_ROOT),
            "logFile": str(log_path.relative_to(REPO_ROOT)),
        }
        save(flow, data)
        threading.Thread(target=_watch, args=(flow, run_id, proc), daemon=True).start()
        threading.Thread(target=_enforce_specification_approval_gate, args=(flow, proc), daemon=True).start()
        return data


def stop(flow: str, grace_seconds: float = 8.0) -> dict[str, Any]:
    with _lock_for(flow):
        data = load(flow)
        if not data or not is_alive(data.get("pid")):
            raise ProcessNotFound(f"no running process for flow '{flow}'")
        pid = data["pid"]
        try:
            os.killpg(os.getpgid(pid), signal.SIGTERM)
        except (ProcessLookupError, PermissionError):
            pass
        data["status"] = "stopping"
        save(flow, data)

    deadline = time.time() + grace_seconds
    while time.time() < deadline and is_alive(pid):
        time.sleep(0.3)
    if is_alive(pid):
        try:
            os.killpg(os.getpgid(pid), signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
        deadline = time.time() + 3
        while time.time() < deadline and is_alive(pid):
            time.sleep(0.2)

    return status(flow)


def read_logs(flow: str, since: int) -> dict[str, Any]:
    data = load(flow)
    if not data:
        return {"offset": 0, "text": "", "status": "idle", "logFile": None}
    log_path = REPO_ROOT / data["logFile"]
    if not log_path.is_file():
        return {"offset": 0, "text": "", "status": data.get("status", "idle"), "logFile": data["logFile"]}
    size = log_path.stat().st_size
    start_offset = max(0, min(since, size))
    with open(log_path, "rb") as fh:
        fh.seek(start_offset)
        chunk = fh.read()
    return {
        "offset": start_offset + len(chunk),
        "text": chunk.decode("utf-8", errors="replace"),
        "status": data.get("status", "idle"),
        "logFile": data["logFile"],
    }
