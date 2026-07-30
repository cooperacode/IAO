#!/usr/bin/env python3
"""Emit current Codex context usage in the harness-neutral contract.

Only this adapter and codex_usage.py know the Codex rollout representation.
Harness.Engine receives the stable JSON contract and never inspects provider files.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path

import codex_usage


def latest_context_usage(session_id: str | None, repo: Path | None) -> dict[str, object] | None:
    candidates: list[codex_usage.UsageEvent] = []
    paths = list(codex_usage.walk_rollouts(codex_usage.codex_home()))
    if session_id:
        session_paths = [path for path in paths if session_id in path.name]
        if session_paths:
            paths = session_paths

    for path in paths:
        session, events = codex_usage.load_rollout_events(path)
        if session_id and session.session_id != session_id:
            continue
        if repo is not None and not codex_usage.session_matches_repo(session, repo):
            continue
        candidates.extend(events)

    if not candidates:
        return None

    event = max(candidates, key=lambda item: item.timestamp)
    if event.context_window is None or event.context_used_tokens is None:
        return None
    if event.context_window <= 0 or event.context_used_tokens < 0:
        return None

    return {
        "schema": "iao.context.v1",
        "sessionId": event.session_id,
        "contextWindowTokens": event.context_window,
        "contextUsedTokens": event.context_used_tokens,
        "source": "codex-rollout-adapter",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--session", default=os.environ.get("CODEX_THREAD_ID"))
    parser.add_argument("--repo", type=Path, default=codex_usage.repo_root())
    args = parser.parse_args()

    usage = latest_context_usage(args.session, args.repo)
    if usage is not None:
        print(json.dumps(usage, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
