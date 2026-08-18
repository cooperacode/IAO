#!/usr/bin/env python3
"""Emit current Kimi Code CLI context usage in the harness-neutral contract.

Only this adapter and kimi_usage.py know the Kimi wire.jsonl representation.
Harness.Engine receives the stable JSON contract and never inspects provider files.

Unlike claude_context_usage.py (which has no configured-window field available
in Claude Code's transcripts and falls back to an env-var default), Kimi's
wire.jsonl reports the model's actual `maxTokens` per request (see
kimi_usage.iter_context_events) -- no default-window guessing needed here.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import kimi_usage


def latest_context_usage(session_id: str | None, repo: Path | None) -> dict[str, object] | None:
    events = [
        event
        for event in kimi_usage.iter_context_events(
            kimi_usage.kimi_code_home(),
            repo=repo,
            session_filter=session_id,
        )
        if event.context_window and event.used_tokens is not None
    ]
    if not events:
        return None

    event = max(events, key=lambda e: e.timestamp)
    if event.context_window <= 0 or event.used_tokens < 0:
        return None

    return {
        "schema": "iao.context.v1",
        "sessionId": event.session_id,
        "contextWindowTokens": event.context_window,
        "contextUsedTokens": event.used_tokens,
        "source": "kimi-wire-adapter",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    # No known env var carries the current session id (unlike
    # CLAUDE_CODE_SESSION_ID/CODEX_THREAD_ID) -- absent a --session filter,
    # this picks the newest matching event across all sessions for --repo,
    # same fallback claude_context_usage.py uses.
    parser.add_argument("--session", default=None)
    parser.add_argument("--repo", type=Path, default=kimi_usage.repo_root())
    args = parser.parse_args()

    usage = latest_context_usage(args.session, args.repo)
    if usage is not None:
        print(json.dumps(usage, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
