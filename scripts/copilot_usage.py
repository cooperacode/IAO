#!/usr/bin/env python3
"""Extracts GitHub Copilot Chat (VS Code) token usage from the local
storage under ~/Library/Application Support/Code/User/workspaceStorage/.

Unlike claude_usage.py and codex_usage.py, Copilot does NOT have a single
reliable, always-present local source of per-session token counts. There
are two layers, in order of reliability, and this script falls back from
the most reliable to the least reliable per session (it never mixes the
two within the same session):

1. debug-log (GitHub.copilot-chat/debug-logs/<sessionId>/main.jsonl) --
   only exists if the user has manually turned on the undocumented setting
   `github.copilot.chat.agentDebugLog.fileLogging.enabled` in settings.json.
   `llm_request` events carry inputTokens/outputTokens/cachedTokens and the
   exact model id for each call.
2. chatSessions (chatSessions/<sessionId>.jsonl or .json) -- always exists,
   but only has promptTokens/completionTokens once the extension version
   that wrote the session started including those fields. The .jsonl
   format is an incremental log (snapshot + patches, see
   reduce_chat_session_jsonl); the older .json format is a single object
   with no tokens.
3. no-tokens -- the session shows up with only a request/timestamp count.

There is no dollar cost estimate: Copilot bills by "premium request" with
a per-model multiplier, not by token, so converting tokens into $ would
produce a number that doesn't match the real invoice. When available, the
raw multiplier/credits show up in the "Premium" column.

Out of scope: the standalone Copilot CLI (~/.copilot/session-state/*.jsonl)
-- this covers VS Code Copilot Chat only.

Usage:
    scripts/copilot_usage.py
    scripts/copilot_usage.py --by-session
    scripts/copilot_usage.py --session <uuid> --json
    scripts/copilot_usage.py --all-repos --since 2026-07-01
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable
from urllib.parse import unquote, urlparse

SOURCE_DEBUG_LOG = "debug-log"
SOURCE_CHAT_SESSIONS = "chatSessions"
SOURCE_NONE = "no-tokens"
UNKNOWN_MODEL = "<unknown>"


@dataclass
class UsageTotals:
    input_tokens: int = 0
    output_tokens: int = 0
    cached_tokens: int | None = None
    first_ts: str | None = None
    last_ts: str | None = None

    def add(self, usage: dict, timestamp: str | None) -> None:
        self.input_tokens += usage.get("input_tokens", 0) or 0
        self.output_tokens += usage.get("output_tokens", 0) or 0
        cached = usage.get("cached_tokens")
        if cached is not None:
            self.cached_tokens = (self.cached_tokens or 0) + cached
        if timestamp:
            if self.first_ts is None or timestamp < self.first_ts:
                self.first_ts = timestamp
            if self.last_ts is None or timestamp > self.last_ts:
                self.last_ts = timestamp

    def merge(self, other: "UsageTotals") -> None:
        self.input_tokens += other.input_tokens
        self.output_tokens += other.output_tokens
        if other.cached_tokens is not None:
            self.cached_tokens = (self.cached_tokens or 0) + other.cached_tokens
        for ts in (other.first_ts,):
            if ts and (self.first_ts is None or ts < self.first_ts):
                self.first_ts = ts
        for ts in (other.last_ts,):
            if ts and (self.last_ts is None or ts > self.last_ts):
                self.last_ts = ts

    @property
    def total_tokens(self) -> int:
        return self.input_tokens + self.output_tokens


@dataclass(frozen=True)
class UsageEvent:
    session_id: str
    agent_label: str
    model: str | None
    usage: dict
    timestamp: str | None
    source: str
    premium_details: str | None = None


@dataclass
class SessionUsage:
    session_id: str
    workspace_dir: Path
    source: str = SOURCE_NONE
    request_count: int = 0
    totals: dict[str, UsageTotals] = field(default_factory=lambda: defaultdict(UsageTotals))
    premium_details_seen: set[str] = field(default_factory=set)
    events: list[UsageEvent] = field(default_factory=list)
    first_ts: str | None = None
    last_ts: str | None = None


# ---------------------------------------------------------------------------
# Workspace discovery / repo filtering
# ---------------------------------------------------------------------------


def default_vscode_user_dir() -> Path:
    return Path.home() / "Library" / "Application Support" / "Code" / "User"


def repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def _resolve(p: str | Path) -> Path:
    return Path(p).expanduser().resolve(strict=False)


def _is_relative_to(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
    except ValueError:
        return False
    return True


def iter_workspace_dirs(vscode_user_dir: Path) -> Iterable[Path]:
    storage = vscode_user_dir / "workspaceStorage"
    if not storage.is_dir():
        return
    for entry in sorted(storage.iterdir()):
        if entry.is_dir():
            yield entry


def read_workspace_folder(workspace_dir: Path) -> Path | None:
    workspace_file = workspace_dir / "workspace.json"
    if not workspace_file.is_file():
        return None
    try:
        data = json.loads(workspace_file.read_text())
    except (OSError, json.JSONDecodeError):
        return None
    folder = data.get("folder") if isinstance(data, dict) else None
    if not isinstance(folder, str) or not folder.startswith("file://"):
        return None
    return Path(unquote(urlparse(folder).path))


def workspace_matches_repo(folder: Path | None, repo: Path | None) -> bool:
    if repo is None:
        return True
    if folder is None:
        return False
    return folder == repo or _is_relative_to(folder, repo)


def iter_session_ids(workspace_dir: Path) -> list[str]:
    ids: set[str] = set()
    debug_logs = workspace_dir / "GitHub.copilot-chat" / "debug-logs"
    if debug_logs.is_dir():
        for entry in debug_logs.iterdir():
            if entry.is_dir() and (entry / "main.jsonl").is_file():
                ids.add(entry.name)
    chat_sessions = workspace_dir / "chatSessions"
    if chat_sessions.is_dir():
        for entry in chat_sessions.iterdir():
            if entry.suffix in (".jsonl", ".json"):
                ids.add(entry.stem)
    return sorted(ids)


def find_chat_session_path(workspace_dir: Path, session_id: str) -> Path | None:
    jsonl_path = workspace_dir / "chatSessions" / f"{session_id}.jsonl"
    if jsonl_path.is_file():
        return jsonl_path
    json_path = workspace_dir / "chatSessions" / f"{session_id}.json"
    if json_path.is_file():
        return json_path
    return None


# ---------------------------------------------------------------------------
# Layer 1: debug-logs/<sessionId>/main.jsonl (+ child logs, e.g. title generation)
# ---------------------------------------------------------------------------


def _epoch_ms_to_iso(ms: float) -> str:
    return datetime.fromtimestamp(ms / 1000, tz=timezone.utc).isoformat()


def _parse_debug_log_file(
    path: Path,
    session_id: str,
    agent_label: str,
    since: str | None,
    until: str | None,
    warnings: list[str],
) -> tuple[list[UsageEvent], list[tuple[str, str]]]:
    events: list[UsageEvent] = []
    child_refs: list[tuple[str, str]] = []
    try:
        lines = path.read_text().splitlines()
    except OSError as exc:
        warnings.append(f"could not read {path}: {exc}")
        return events, child_refs

    for line in lines:
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue

        typ = obj.get("type")
        attrs = obj.get("attrs") or {}

        if typ == "child_session_ref":
            child_log = attrs.get("childLogFile")
            label = attrs.get("label") or attrs.get("childSessionId") or "child"
            if child_log:
                child_refs.append((str(label), str(child_log)))
            continue

        if typ != "llm_request":
            continue

        input_tokens = attrs.get("inputTokens")
        output_tokens = attrs.get("outputTokens")
        if input_tokens is None and output_tokens is None:
            continue

        ts_raw = obj.get("ts")
        ts = _epoch_ms_to_iso(ts_raw) if isinstance(ts_raw, (int, float)) else None
        if since and ts and ts < since:
            continue
        if until and ts and ts > until:
            continue

        events.append(
            UsageEvent(
                session_id=session_id,
                agent_label=agent_label,
                model=attrs.get("model"),
                usage={
                    "input_tokens": input_tokens or 0,
                    "output_tokens": output_tokens or 0,
                    "cached_tokens": attrs.get("cachedTokens"),
                },
                timestamp=ts,
                source=SOURCE_DEBUG_LOG,
            )
        )

    return events, child_refs


def walk_debug_log_session(
    session_dir: Path,
    session_id: str,
    since: str | None,
    until: str | None,
    warnings: list[str],
) -> list[UsageEvent]:
    events: list[UsageEvent] = []
    queue: list[tuple[str, Path]] = [("main", session_dir / "main.jsonl")]
    visited: set[Path] = set()

    while queue:
        label, path = queue.pop(0)
        if path in visited or not path.is_file():
            continue
        visited.add(path)
        file_events, child_refs = _parse_debug_log_file(
            path, session_id, label, since, until, warnings
        )
        events.extend(file_events)
        for child_label, child_log in child_refs:
            queue.append((child_label, session_dir / child_log))

    return events


# ---------------------------------------------------------------------------
# Layer 2: chatSessions/<sessionId>.jsonl (incremental log) or .json (legacy)
# ---------------------------------------------------------------------------


def _navigate_parent(root: dict, path: list):
    node = root
    for seg in path[:-1]:
        node = node[seg]
    return node, path[-1]


def _apply_set(state: dict, path: list, value) -> None:
    if not path:
        return
    parent, last = _navigate_parent(state, path)
    parent[last] = value


def _apply_splice(state: dict, path: list, index: int | None, items: list) -> None:
    if not path:
        return
    parent, last = _navigate_parent(state, path)
    if isinstance(parent, dict) and last not in parent:
        parent[last] = []
    target = parent[last]
    if index is None:
        target.extend(items)
    else:
        target[index:index] = items


def reduce_chat_session_jsonl(path: Path, warnings: list[str]) -> dict:
    """Reconstructs the session document from the operation sequence:
    kind 0 = initial snapshot (replaces everything), kind 1 = set (parent[k[-1]] = v),
    kind 2 = array splice (target[i:i] = v, or append if `i` is absent)."""
    state: dict = {}
    try:
        lines = path.read_text().splitlines()
    except OSError as exc:
        warnings.append(f"could not read {path}: {exc}")
        return state

    for lineno, line in enumerate(lines, start=1):
        line = line.strip()
        if not line:
            continue
        try:
            op = json.loads(line)
        except json.JSONDecodeError:
            warnings.append(f"invalid line at {path.name}:{lineno}")
            continue

        kind = op.get("kind")
        if kind == 0:
            state = op.get("v") or {}
            continue

        k = op.get("k") or []
        try:
            if kind == 1:
                _apply_set(state, k, op.get("v"))
            elif kind == 2:
                _apply_splice(state, k, op.get("i"), op.get("v") or [])
            else:
                warnings.append(f"unknown kind {kind} at {path.name}:{lineno}")
        except (KeyError, IndexError, TypeError) as exc:
            warnings.append(
                f"failed to apply op kind={kind} at {path.name}:{lineno}: {exc}"
            )

    return state


def load_chat_session_state(path: Path, warnings: list[str]) -> dict:
    if path.suffix == ".json":
        try:
            data = json.loads(path.read_text())
        except (OSError, json.JSONDecodeError) as exc:
            warnings.append(f"could not read {path}: {exc}")
            return {}
        return data if isinstance(data, dict) else {}
    return reduce_chat_session_jsonl(path, warnings)


_PREMIUM_DETAILS_RE = re.compile(r"^(?P<label>.+?)\s*•\s*(?P<suffix>.+)$")
_MULTIPLIER_RE = re.compile(r"^([\d.]+)x$", re.IGNORECASE)


def parse_premium_details(details: str | None) -> tuple[str | None, float | None]:
    """E.g.: 'GPT-4.1 • 0x' -> ('GPT-4.1', 0.0). 'Claude Opus • 3.5 credits'
    -> ('Claude Opus', None) -- fractional credits don't turn into a multiplier."""
    if not details:
        return None, None
    match = _PREMIUM_DETAILS_RE.match(details.strip())
    if not match:
        return details.strip(), None
    label = match.group("label").strip()
    suffix = match.group("suffix").strip()
    mult_match = _MULTIPLIER_RE.match(suffix)
    if mult_match:
        return label, float(mult_match.group(1))
    return label, None


def _request_token_counts(req: dict) -> tuple[float | None, float | None]:
    """Tokens show up in one of two places depending on the extension version
    that wrote the session: directly at requests[i].promptTokens/completionTokens,
    or nested at requests[i].result.metadata.promptTokens/outputTokens."""
    prompt = req.get("promptTokens")
    completion = req.get("completionTokens")
    if isinstance(prompt, (int, float)) and isinstance(completion, (int, float)):
        return prompt, completion

    metadata = (req.get("result") or {}).get("metadata") or {}
    meta_prompt = metadata.get("promptTokens")
    meta_completion = metadata.get("outputTokens")
    if isinstance(meta_prompt, (int, float)) and isinstance(meta_completion, (int, float)):
        return meta_prompt, meta_completion

    return None, None


def _request_has_tokens(req: dict) -> bool:
    prompt, completion = _request_token_counts(req)
    return prompt is not None and completion is not None


def _request_model(req: dict) -> str | None:
    result = req.get("result") or {}
    metadata = result.get("metadata") or {}
    label, _ = parse_premium_details(result.get("details"))
    return metadata.get("resolvedModel") or req.get("modelId") or label


def _request_timestamp(req: dict) -> str | None:
    ts_raw = req.get("timestamp")
    if ts_raw is None:
        return None
    if isinstance(ts_raw, (int, float)):
        return _epoch_ms_to_iso(ts_raw)
    return str(ts_raw)


def chat_session_events(
    requests: list[dict],
    session_id: str,
    since: str | None,
    until: str | None,
) -> list[UsageEvent]:
    events: list[UsageEvent] = []
    for req in requests:
        if not isinstance(req, dict) or not _request_has_tokens(req):
            continue
        ts = _request_timestamp(req)
        if since and ts and ts < since:
            continue
        if until and ts and ts > until:
            continue
        result = req.get("result") or {}
        prompt, completion = _request_token_counts(req)
        events.append(
            UsageEvent(
                session_id=session_id,
                agent_label="main",
                model=_request_model(req),
                usage={
                    "input_tokens": prompt or 0,
                    "output_tokens": completion or 0,
                    "cached_tokens": None,
                },
                timestamp=ts,
                source=SOURCE_CHAT_SESSIONS,
                premium_details=result.get("details"),
            )
        )
    return events


# ---------------------------------------------------------------------------
# Per-session unification: debug-log > chatSessions > no-tokens
# ---------------------------------------------------------------------------


def _remember_ts(session: SessionUsage, ts: str | None) -> None:
    if not ts:
        return
    if session.first_ts is None or ts < session.first_ts:
        session.first_ts = ts
    if session.last_ts is None or ts > session.last_ts:
        session.last_ts = ts


def collect_session(
    session_id: str,
    workspace_dir: Path,
    since: str | None,
    until: str | None,
    warnings: list[str],
) -> SessionUsage:
    session = SessionUsage(session_id=session_id, workspace_dir=workspace_dir)

    debug_dir = workspace_dir / "GitHub.copilot-chat" / "debug-logs" / session_id
    debug_events: list[UsageEvent] = []
    if (debug_dir / "main.jsonl").is_file():
        debug_events = walk_debug_log_session(debug_dir, session_id, since, until, warnings)

    chat_path = find_chat_session_path(workspace_dir, session_id)
    requests: list[dict] = []
    if chat_path is not None:
        state = load_chat_session_state(chat_path, warnings)
        raw_requests = state.get("requests")
        requests = raw_requests if isinstance(raw_requests, list) else []
    session.request_count = len(requests)

    if debug_events:
        session.source = SOURCE_DEBUG_LOG
        chosen_events = debug_events
    else:
        chosen_events = chat_session_events(requests, session_id, since, until)
        session.source = SOURCE_CHAT_SESSIONS if chosen_events else SOURCE_NONE

    session.events = chosen_events
    for event in chosen_events:
        model_key = event.model or UNKNOWN_MODEL
        session.totals[model_key].add(event.usage, event.timestamp)
        _remember_ts(session, event.timestamp)
        if event.premium_details:
            session.premium_details_seen.add(event.premium_details)

    if not chosen_events:
        for req in requests:
            ts = _request_timestamp(req)
            if since and ts and ts < since:
                continue
            if until and ts and ts > until:
                continue
            _remember_ts(session, ts)

    return session


def collect_sessions(
    vscode_user_dir: Path,
    repo: Path | None,
    session_filter: str | None = None,
    since: str | None = None,
    until: str | None = None,
) -> tuple[list[SessionUsage], list[str]]:
    warnings: list[str] = []
    sessions: list[SessionUsage] = []

    for workspace_dir in iter_workspace_dirs(vscode_user_dir):
        folder = read_workspace_folder(workspace_dir)
        if not workspace_matches_repo(folder, repo):
            continue
        for session_id in iter_session_ids(workspace_dir):
            if session_filter and session_id != session_filter:
                continue
            sessions.append(
                collect_session(session_id, workspace_dir, since, until, warnings)
            )

    return sorted(sessions, key=lambda s: s.first_ts or ""), warnings


def iter_usage_events(
    vscode_user_dir: Path,
    repo: Path | None = None,
    session_filter: str | None = None,
    since: str | None = None,
    until: str | None = None,
    warnings: list[str] | None = None,
):
    """Yields raw events: (session_id, agent_label, model, usage, timestamp, source,
    premium_details). Reusable by other tools, in the same spirit as the
    iter_usage_events in claude_usage.py/codex_usage.py."""
    sessions, collected_warnings = collect_sessions(
        vscode_user_dir, repo, session_filter, since, until
    )
    if warnings is not None:
        warnings.extend(collected_warnings)

    for session in sessions:
        for event in session.events:
            yield (
                event.session_id,
                event.agent_label,
                event.model,
                event.usage,
                event.timestamp,
                event.source,
                event.premium_details,
            )


# ---------------------------------------------------------------------------
# Aggregation and rendering
# ---------------------------------------------------------------------------


def per_model_with_sources(
    sessions: list[SessionUsage],
) -> tuple[dict[str, UsageTotals], dict[str, set[str]]]:
    totals: dict[str, UsageTotals] = defaultdict(UsageTotals)
    sources: dict[str, set[str]] = defaultdict(set)
    for session in sessions:
        for model, usage in session.totals.items():
            totals[model].merge(usage)
            sources[model].add(session.source)
    return totals, sources


def per_source_counts(sessions: list[SessionUsage]) -> dict[str, int]:
    counts: dict[str, int] = defaultdict(int)
    for session in sessions:
        counts[session.source] += 1
    return counts


def print_table(rows: list[list[str]], headers: list[str]) -> None:
    widths = [len(h) for h in headers]
    for row in rows:
        for i, cell in enumerate(row):
            widths[i] = max(widths[i], len(cell))
    fmt = "  ".join(f"{{:<{w}}}" for w in widths)
    print(fmt.format(*headers))
    print(fmt.format(*("-" * w for w in widths)))
    for row in rows:
        print(fmt.format(*row))


def render_model_table(sessions: list[SessionUsage]) -> None:
    by_model, sources = per_model_with_sources(sessions)
    rows = []
    grand = UsageTotals()
    for model in sorted(by_model, key=lambda m: -by_model[m].total_tokens):
        usage = by_model[model]
        grand.merge(usage)
        cached = f"{usage.cached_tokens:,}" if usage.cached_tokens is not None else "n/a"
        rows.append(
            [
                model,
                f"{usage.input_tokens:,}",
                f"{usage.output_tokens:,}",
                cached,
                f"{usage.total_tokens:,}",
                ", ".join(sorted(sources[model])),
            ]
        )
    print_table(rows, ["Model", "Input", "Output", "Cached", "Total", "Source"])
    print(f"\nGrand total: {grand.total_tokens:,} tokens (no cost estimate)")


def render_session_table(sessions: list[SessionUsage]) -> None:
    rows = []
    for session in sessions:
        models = ", ".join(sorted(session.totals.keys())) or "-"
        total = sum(usage.total_tokens for usage in session.totals.values())
        premium = "; ".join(sorted(session.premium_details_seen)) or "-"
        rows.append(
            [
                session.session_id,
                session.first_ts or "?",
                session.last_ts or "?",
                models,
                f"{session.request_count:,}",
                f"{total:,}",
                premium,
                session.source,
            ]
        )
    print_table(
        rows,
        ["Session", "First", "Last", "Model(s)", "Requests", "Total", "Premium", "Source"],
    )

    counts = per_source_counts(sessions)
    print("\nSessions by source:")
    for source in (SOURCE_DEBUG_LOG, SOURCE_CHAT_SESSIONS, SOURCE_NONE):
        if counts.get(source):
            print(f"  {source}: {counts[source]}")


def to_jsonable_totals(usage: UsageTotals) -> dict:
    return {
        "input_tokens": usage.input_tokens,
        "output_tokens": usage.output_tokens,
        "cached_tokens": usage.cached_tokens,
        "total_tokens": usage.total_tokens,
        "first_ts": usage.first_ts,
        "last_ts": usage.last_ts,
    }


def render_json(sessions: list[SessionUsage], warnings: list[str]) -> None:
    by_model, sources = per_model_with_sources(sessions)
    output = {
        "per_model": {
            model: {**to_jsonable_totals(usage), "sources": sorted(sources[model])}
            for model, usage in by_model.items()
        },
        "per_session": {
            session.session_id: {
                "workspace": str(session.workspace_dir),
                "source": session.source,
                "request_count": session.request_count,
                "first_ts": session.first_ts,
                "last_ts": session.last_ts,
                "premium_details": sorted(session.premium_details_seen),
                "totals": {
                    model: to_jsonable_totals(usage)
                    for model, usage in session.totals.items()
                },
            }
            for session in sessions
        },
        "by_source": dict(per_source_counts(sessions)),
        "cost_note": "Copilot bills by premium request with a multiplier, not by token -- no dollar cost estimate.",
        "warnings": warnings,
    }
    print(json.dumps(output, indent=2, ensure_ascii=False))


def main() -> None:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "--vscode-user-dir",
        type=Path,
        default=None,
        help="Override the VS Code User directory (default: ~/Library/Application Support/Code/User)",
    )
    parser.add_argument(
        "--repo",
        type=Path,
        default=None,
        help="Filter sessions whose workspace is inside this repo (default: current repo)",
    )
    parser.add_argument(
        "--all-repos", action="store_true", help="Don't filter by repo/workspace"
    )
    parser.add_argument("--session", default=None, help="Filter by session id")
    parser.add_argument("--since", default=None, help="Minimum date, ISO or YYYY-MM-DD")
    parser.add_argument("--until", default=None, help="Maximum date, ISO or YYYY-MM-DD")
    parser.add_argument(
        "--by-session", action="store_true", help="Show a per-session table"
    )
    parser.add_argument("--json", action="store_true", help="Output as JSON")
    args = parser.parse_args()

    user_dir = args.vscode_user_dir or default_vscode_user_dir()
    if not user_dir.is_dir():
        print(f"VS Code User directory not found: {user_dir}", file=sys.stderr)
        sys.exit(1)

    repo = None if args.all_repos else _resolve(args.repo or repo_root())
    sessions, warnings = collect_sessions(
        vscode_user_dir=user_dir,
        repo=repo,
        session_filter=args.session,
        since=args.since,
        until=args.until,
    )

    for warning in warnings:
        print(f"Warning: {warning}", file=sys.stderr)

    if args.json:
        render_json(sessions, warnings)
        return

    if args.by_session:
        render_session_table(sessions)
    else:
        render_model_table(sessions)


if __name__ == "__main__":
    main()
