#!/usr/bin/env python3
"""Extracts token usage and estimated cost from local Kimi Code CLI sessions.

Kimi (`kimi`, Moonshot AI) writes one directory per session under:

    $KIMI_CODE_HOME/sessions/<workdir-hash>/<sessionId>/
        state.json                 # cwd, createdAt/updatedAt, agents map
        agents/<agentLabel>/wire.jsonl   # raw per-turn wire log (main + any subagents)

and a flat index at `$KIMI_CODE_HOME/session_index.jsonl` mapping each
sessionId to its sessionDir and workDir (cwd) -- used here the same way
`--repo` filters Codex rollouts by cwd.

`agents/*/wire.jsonl` is an undocumented internal format (reverse-engineered
by running real sessions, the same way codex_usage.py's rollout parser was
built against Codex's undocumented rollout-*.jsonl) -- it may change between
`kimi` versions. Each line is one wire event; the ones this script cares
about have `"type": "usage.record"`:

    {"type":"usage.record","model":"kimi-code/kimi-for-coding",
     "usage":{"inputOther":2226,"output":242,"inputCacheRead":17920,
     "inputCacheCreation":0},"usageScope":"turn","time":1786841774258}

Unlike Codex's cumulative-snapshot counters, each `usage.record` is already
the delta for that request -- no reset/delta bookkeeping needed.

`iter_context_events()` (used by kimi_context_usage.py, not by the cost CLI
below) also reads `"type": "llm.request"` lines for their `maxTokens` field --
the model's actual configured context window for that turn.

Usage:
    .harness/scripts/kimi_usage.py
    .harness/scripts/kimi_usage.py --by-session
    .harness/scripts/kimi_usage.py --session <sessionId> --json
    .harness/scripts/kimi_usage.py --all-repos --since 2026-08-01
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

UNKNOWN_MODEL = "<unknown>"


@dataclass(frozen=True)
class ModelPrice:
    input: float  # $ / 1M non-cached input tokens
    output: float  # $ / 1M output tokens
    cache_read: float | None  # $ / 1M cache-read input tokens
    cache_write: float | None = None  # $ / 1M cache-write input tokens


# ---------------------------------------------------------------------------
# PRICING TABLE -- USD per 1,000,000 tokens. Source: the `moonshotai` provider
# in https://models.dev/api.json (the same public catalog `kimi provider
# catalog` reads), checked on 2026-08-15.
#
# `kimi`'s default managed/OAuth provider (config.toml `[providers."managed:
# kimi-code"]`, model aliases under the "kimi-code/" namespace, e.g.
# "kimi-code/kimi-for-coding") reports cost 0 in that same catalog -- it's a
# flat-rate plan (like a Claude Code/Copilot subscription), not metered by
# token. The prices below are the *raw pay-as-you-go API* prices for the
# model each managed alias is believed to run on, matched by context window
# size and name pattern (Moonshot doesn't publish this correspondence
# officially) -- same "API-like estimate, not a billed-cost reconciliation"
# caveat codex_usage.py documents for ChatGPT-authenticated Codex sessions.
#
# No distinct cache-write price is published for these models; cache-creation
# tokens are priced at the standard input rate below (`cache_write=None` ->
# UsageTotals.cost falls back to `price.input`).
#
# "kimi-code/k3-256k" has no confident raw-API counterpart (no exact
# name/context match in the catalog) and is deliberately left out --
# UsageTotals.cost returns None (shown as "n/a") for it, same treatment the
# other scripts give any unknown model.
# ---------------------------------------------------------------------------
PRICING: dict[str, ModelPrice] = {
    # -> moonshotai "kimi-k2.7-code" (ctx 262144, non-highspeed "coding" variant)
    "kimi-code/kimi-for-coding": ModelPrice(input=0.95, output=4.00, cache_read=0.19),
    # -> moonshotai "kimi-k2.7-code-highspeed" (ctx 262144, "highspeed" suffix matches)
    "kimi-code/kimi-for-coding-highspeed": ModelPrice(input=1.90, output=8.00, cache_read=0.38),
    # -> moonshotai "kimi-k3" (ctx 1048576, exact context-window match)
    "kimi-code/k3": ModelPrice(input=3.00, output=15.00, cache_read=0.30),
}


@dataclass
class UsageTotals:
    input_other: int = 0
    input_cache_read: int = 0
    input_cache_creation: int = 0
    output_tokens: int = 0
    first_ts: str | None = None
    last_ts: str | None = None

    def add(self, usage: dict, timestamp: str | None) -> None:
        self.input_other += usage.get("inputOther", 0) or 0
        self.input_cache_read += usage.get("inputCacheRead", 0) or 0
        self.input_cache_creation += usage.get("inputCacheCreation", 0) or 0
        self.output_tokens += usage.get("output", 0) or 0
        if timestamp:
            if self.first_ts is None or timestamp < self.first_ts:
                self.first_ts = timestamp
            if self.last_ts is None or timestamp > self.last_ts:
                self.last_ts = timestamp

    def merge(self, other: "UsageTotals") -> None:
        self.input_other += other.input_other
        self.input_cache_read += other.input_cache_read
        self.input_cache_creation += other.input_cache_creation
        self.output_tokens += other.output_tokens
        for ts in (other.first_ts,):
            if ts and (self.first_ts is None or ts < self.first_ts):
                self.first_ts = ts
        for ts in (other.last_ts,):
            if ts and (self.last_ts is None or ts > self.last_ts):
                self.last_ts = ts

    @property
    def total_tokens(self) -> int:
        return (
            self.input_other
            + self.input_cache_read
            + self.input_cache_creation
            + self.output_tokens
        )

    def cost(self, model: str) -> float | None:
        price = PRICING.get(model)
        if price is None:
            return None
        if self.input_cache_read and price.cache_read is None:
            return None
        cache_write_rate = price.input if price.cache_write is None else price.cache_write
        return (
            self.input_other / 1_000_000 * price.input
            + self.input_cache_read / 1_000_000 * (price.cache_read or 0.0)
            + self.input_cache_creation / 1_000_000 * cache_write_rate
            + self.output_tokens / 1_000_000 * price.output
        )


def kimi_code_home() -> Path:
    return Path(os.environ.get("KIMI_CODE_HOME") or Path.home() / ".kimi-code")


def repo_root() -> Path:
    # This script lives in `.harness/scripts`; the repository is three levels up.
    return Path(__file__).resolve().parents[2]


def _resolve(p: str | Path) -> Path:
    return Path(p).expanduser().resolve(strict=False)


def _is_relative_to(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
    except ValueError:
        return False
    return True


def load_session_index(home: Path) -> dict[str, dict[str, str]]:
    """Parses session_index.jsonl into {sessionId: {"sessionDir", "workDir"}}.

    The file is append-only across a session's lifetime (e.g. resumed runs
    add another line), so later lines win when the same sessionId repeats.
    """
    index_path = home / "session_index.jsonl"
    index: dict[str, dict[str, str]] = {}
    if not index_path.is_file():
        return index
    try:
        lines = index_path.read_text().splitlines()
    except OSError:
        return index
    for line in lines:
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        session_id = obj.get("sessionId")
        if not session_id:
            continue
        index[session_id] = {
            "sessionDir": obj.get("sessionDir", ""),
            "workDir": obj.get("workDir", ""),
        }
    return index


def _epoch_ms_to_iso(value: int) -> str:
    from datetime import datetime, timezone

    dt = datetime.fromtimestamp(value / 1000, tz=timezone.utc)
    return dt.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def session_matches_repo(work_dir: str, repo: Path | None) -> bool:
    if repo is None:
        return True
    if not work_dir:
        return False
    cwd = _resolve(work_dir)
    resolved_repo = _resolve(repo)
    return cwd == resolved_repo or _is_relative_to(cwd, resolved_repo)


def walk_wire_logs(session_dir: Path) -> Iterable[tuple[str, Path]]:
    """Yields (agent_label, wire.jsonl path) for a session's main agent and
    any subagents -- `state.json`'s `agents` map lists every one, but
    globbing `agents/*/wire.jsonl` directly avoids depending on that file
    also being present/parseable."""
    agents_dir = session_dir / "agents"
    if not agents_dir.is_dir():
        return
    for wire_path in sorted(agents_dir.glob("*/wire.jsonl")):
        yield wire_path.parent.name, wire_path


def _iter_session_wire_files(
    home: Path,
    repo: Path | None,
    session_filter: str | None,
    warnings: list[str] | None,
) -> Iterable[tuple[str, str, Path]]:
    """Yields (session_id, agent_label, wire.jsonl path) for every session
    matching `repo`/`session_filter` -- the directory-walking/repo-filtering
    logic shared by iter_usage_events (cost) and iter_context_events
    (context-window telemetry), so both read `agents/*/wire.jsonl` the same
    way."""
    index = load_session_index(home)
    sessions_dir = home / "sessions"
    if not sessions_dir.is_dir():
        if warnings is not None:
            warnings.append(f"sessions directory not found: {sessions_dir}")
        return

    for session_dir in sorted(sessions_dir.glob("*/session_*")):
        session_id = session_dir.name
        if session_filter and session_id != session_filter:
            continue

        work_dir = index.get(session_id, {}).get("workDir", "")
        if not work_dir:
            # session_index.jsonl missing this entry (e.g. edited/relocated
            # sessions dir) -- fall back to state.json's own cwd.
            try:
                state = json.loads((session_dir / "state.json").read_text())
                work_dir = state.get("cwd", "")
            except (OSError, json.JSONDecodeError):
                work_dir = ""
        if not session_matches_repo(work_dir, repo):
            continue

        for agent_label, wire_path in walk_wire_logs(session_dir):
            yield session_id, agent_label, wire_path


def _read_wire_lines(wire_path: Path, warnings: list[str] | None) -> list[dict]:
    try:
        raw_lines = wire_path.read_text().splitlines()
    except OSError as exc:
        if warnings is not None:
            warnings.append(f"could not read {wire_path}: {exc}")
        return []
    parsed = []
    for line in raw_lines:
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(obj, dict):
            parsed.append(obj)
    return parsed


def iter_usage_events(
    home: Path,
    repo: Path | None = None,
    session_filter: str | None = None,
    since: str | None = None,
    until: str | None = None,
    warnings: list[str] | None = None,
):
    """Yields raw per-event usage: (session_id, agent_label, model, usage, timestamp).

    `usage` keeps the native `wire.jsonl` field names (inputOther, output,
    inputCacheRead, inputCacheCreation) -- UsageTotals.add() maps them.
    `timestamp` is an ISO-8601 UTC string converted from the wire log's
    epoch-millisecond `time` field.
    """
    for session_id, agent_label, wire_path in _iter_session_wire_files(
        home, repo, session_filter, warnings
    ):
        for obj in _read_wire_lines(wire_path, warnings):
            if obj.get("type") != "usage.record":
                continue

            usage = obj.get("usage")
            model = obj.get("model") or UNKNOWN_MODEL
            raw_time = obj.get("time")
            if not isinstance(usage, dict) or not isinstance(raw_time, (int, float)):
                continue

            ts = _epoch_ms_to_iso(int(raw_time))
            if since and ts < since:
                continue
            if until and ts > until:
                continue

            yield session_id, agent_label, model, usage, ts


@dataclass(frozen=True)
class ContextEvent:
    session_id: str
    agent_label: str
    timestamp: str
    context_window: int | None
    used_tokens: int | None


def iter_context_events(
    home: Path,
    repo: Path | None = None,
    session_filter: str | None = None,
    warnings: list[str] | None = None,
) -> Iterable[ContextEvent]:
    """Yields one ContextEvent per `usage.record`, pairing it with the
    `maxTokens` reported by the most recent preceding `llm.request` in the
    same wire.jsonl (same turn) -- the model's actual configured context
    window, not a guessed default (unlike claude_context_usage.py, which has
    no such field available and falls back to an env-var default).

    `used_tokens` is the current turn's total input (inputOther +
    inputCacheRead + inputCacheCreation) -- the size of what was actually
    sent to the model for that request, same quantity Codex's
    last_token_usage.input_tokens and Claude's context_input_tokens()
    represent.
    """
    for session_id, agent_label, wire_path in _iter_session_wire_files(
        home, repo, session_filter, warnings
    ):
        current_window: int | None = None
        for obj in _read_wire_lines(wire_path, warnings):
            obj_type = obj.get("type")
            if obj_type == "llm.request":
                max_tokens = obj.get("maxTokens")
                if isinstance(max_tokens, int) and max_tokens > 0:
                    current_window = max_tokens
                continue
            if obj_type != "usage.record":
                continue

            usage = obj.get("usage")
            raw_time = obj.get("time")
            if not isinstance(usage, dict) or not isinstance(raw_time, (int, float)):
                continue

            used_tokens = (
                (usage.get("inputOther", 0) or 0)
                + (usage.get("inputCacheRead", 0) or 0)
                + (usage.get("inputCacheCreation", 0) or 0)
            )
            yield ContextEvent(
                session_id=session_id,
                agent_label=agent_label,
                timestamp=_epoch_ms_to_iso(int(raw_time)),
                context_window=current_window,
                used_tokens=used_tokens,
            )


def collect(
    home: Path,
    repo: Path | None = None,
    session_filter: str | None = None,
    since: str | None = None,
    until: str | None = None,
):
    totals: dict[tuple[str, str, str], UsageTotals] = defaultdict(UsageTotals)
    warnings: list[str] = []

    for session_id, agent_label, model, usage, ts in iter_usage_events(
        home, repo, session_filter, since, until, warnings
    ):
        totals[(session_id, agent_label, model)].add(usage, ts)

    return totals, warnings


def per_model(totals: dict) -> dict[str, UsageTotals]:
    out: dict[str, UsageTotals] = defaultdict(UsageTotals)
    for (_, _, model), usage in totals.items():
        out[model].merge(usage)
    return out


def per_session(totals: dict) -> dict[str, UsageTotals]:
    out: dict[str, UsageTotals] = defaultdict(UsageTotals)
    for (session_id, _, _), usage in totals.items():
        out[session_id].merge(usage)
    return out


def per_session_and_agent(totals: dict) -> dict[str, dict[str, UsageTotals]]:
    out: dict[str, dict[str, UsageTotals]] = defaultdict(lambda: defaultdict(UsageTotals))
    for (session_id, agent_label, _), usage in totals.items():
        out[session_id][agent_label].merge(usage)
    return out


def _session_cost(totals: dict, session_id: str) -> tuple[float, list[str]]:
    per_model_totals = per_model({k: v for k, v in totals.items() if k[0] == session_id})
    cost = 0.0
    unpriced: list[str] = []
    for model, usage in per_model_totals.items():
        value = usage.cost(model)
        if value is None:
            unpriced.append(model)
        else:
            cost += value
    return cost, sorted(unpriced)


def fmt_cost(value: float | None) -> str:
    return f"${value:,.4f}" if value is not None else "n/a"


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


def render_model_table(totals: dict) -> None:
    by_model = per_model(totals)
    rows = []
    grand = UsageTotals()
    grand_cost = 0.0
    unpriced: list[str] = []

    for model in sorted(by_model, key=lambda m: -by_model[m].total_tokens):
        usage = by_model[model]
        grand.merge(usage)
        cost = usage.cost(model)
        if cost is None:
            unpriced.append(model)
        else:
            grand_cost += cost
        rows.append(
            [
                model,
                f"{usage.input_other:,}",
                f"{usage.input_cache_read:,}",
                f"{usage.input_cache_creation:,}",
                f"{usage.output_tokens:,}",
                f"{usage.total_tokens:,}",
                fmt_cost(cost),
            ]
        )

    print_table(
        rows,
        ["Model", "Input", "Cache Read", "Cache Write", "Output", "Total", "Cost"],
    )
    print(
        f"\nGrand total: {grand.total_tokens:,} tokens, {fmt_cost(grand_cost)}"
        + (" (partial -- some models have no pricing)" if unpriced else "")
    )
    if unpriced:
        print(
            f"Warning: no pricing registered for: {', '.join(sorted(unpriced))}",
            file=sys.stderr,
        )


def render_session_table(totals: dict, show_subagents: bool) -> None:
    by_session = per_session(totals)
    nested = per_session_and_agent(totals) if show_subagents else {}

    sessions = sorted(by_session, key=lambda s: by_session[s].first_ts or "")
    rows = []
    for session_id in sessions:
        usage = by_session[session_id]
        cost, _ = _session_cost(totals, session_id)
        rows.append(
            [
                session_id,
                usage.first_ts or "?",
                usage.last_ts or "?",
                f"{usage.total_tokens:,}",
                fmt_cost(cost),
            ]
        )
    print_table(rows, ["Session", "First turn", "Last turn", "Total", "Cost"])

    if show_subagents:
        for session_id in sessions:
            agents = nested.get(session_id, {})
            if len(agents) <= 1:
                continue
            print(f"\n  {session_id}:")
            for agent_label in sorted(agents, key=lambda a: (a != "main", a)):
                usage = agents[agent_label]
                print(f"    {agent_label:<40} {usage.total_tokens:>12,} tokens")


def to_jsonable(usage: UsageTotals, model: str | None = None) -> dict:
    d = {
        "input_tokens": usage.input_other,
        "output_tokens": usage.output_tokens,
        "reasoning_output_tokens": 0,  # not broken out separately by Kimi's usage.record
        # Aliases in the format used by codex_usage.py/claude_usage.py -- consumers
        # such as skills/session-report/generate_report.py read TOKEN_FIELDS by
        # these names regardless of driver. input_tokens above already excludes
        # cache (sibling counters, like Claude), so non_cached_input_tokens ==
        # input_tokens.
        "cached_input_tokens": usage.input_cache_read,
        "cache_write_input_tokens": usage.input_cache_creation,
        "non_cached_input_tokens": usage.input_other,
        "total_tokens": usage.total_tokens,
        "first_ts": usage.first_ts,
        "last_ts": usage.last_ts,
    }
    if model is not None:
        d["cost"] = usage.cost(model)
    return d


def render_json(
    totals: dict,
    home: Path,
    repo: Path | None,
    warnings: list[str],
    session_filter: str | None = None,
) -> None:
    by_model = per_model(totals)
    by_session = per_session(totals)
    nested = per_session_and_agent(totals)

    unpriced = sorted(m for m in by_model if by_model[m].cost(m) is None)

    per_session_json = {}
    for session_id, usage in by_session.items():
        cost, session_unpriced = _session_cost(totals, session_id)
        per_session_json[session_id] = {
            "first_ts": usage.first_ts,
            "last_ts": usage.last_ts,
            "totals": to_jsonable(usage),
            "cost": cost,
            "unpriced_models": session_unpriced,
            "agents": {a: to_jsonable(au) for a, au in nested.get(session_id, {}).items()},
        }

    output = {
        "kimi_code_home": str(home),
        "repo_filter": str(repo) if repo else None,
        "session_filter": session_filter,
        "pricing": {
            "currency": "USD",
            "unit": "per 1M tokens",
            "source": ".harness/scripts/kimi_usage.py",
        },
        "per_model": {model: to_jsonable(usage, model) for model, usage in by_model.items()},
        "per_session": per_session_json,
        "unpriced_models": unpriced,
        "warnings": warnings,
    }
    print(json.dumps(output, indent=2, ensure_ascii=False))


def main() -> None:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "--kimi-home",
        type=Path,
        default=None,
        help="Override KIMI_CODE_HOME (default: $KIMI_CODE_HOME or ~/.kimi-code)",
    )
    parser.add_argument(
        "--repo",
        type=Path,
        default=None,
        help="Filter sessions whose workDir is inside this repo (default: current repo)",
    )
    parser.add_argument("--all-repos", action="store_true", help="Do not filter by repo/cwd")
    parser.add_argument("--session", default=None, help="Filter by a specific session id")
    parser.add_argument("--since", default=None, help="Minimum timestamp, ISO-8601")
    parser.add_argument("--until", default=None, help="Maximum timestamp, ISO-8601")
    parser.add_argument("--by-session", action="store_true", help="Show table grouped by session")
    parser.add_argument(
        "--show-subagents",
        action="store_true",
        help="Break down subagents (implies --by-session)",
    )
    parser.add_argument("--json", action="store_true", help="Output as JSON")
    args = parser.parse_args()

    home = args.kimi_home or kimi_code_home()
    if not home.is_dir():
        print(f"KIMI_CODE_HOME not found: {home}", file=sys.stderr)
        sys.exit(1)

    repo = None if args.all_repos else _resolve(args.repo or repo_root())
    totals, warnings = collect(
        home,
        repo=repo,
        session_filter=args.session,
        since=args.since,
        until=args.until,
    )

    for warning in warnings:
        print(f"Warning: {warning}", file=sys.stderr)

    if args.json:
        render_json(totals, home, repo, warnings, args.session)
        return

    show_subagents = args.show_subagents
    if args.by_session or show_subagents:
        render_session_table(totals, show_subagents)
    else:
        render_model_table(totals)


if __name__ == "__main__":
    main()
