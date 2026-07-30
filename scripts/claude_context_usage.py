#!/usr/bin/env python3
"""Emit current Claude Code context usage in the harness-neutral contract."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path

import claude_usage

DEFAULT_CONTEXT_WINDOW_TOKENS = 200_000


def configured_context_window() -> int:
    raw = (
        os.environ.get("CLAUDE_CONTEXT_WINDOW_TOKENS")
        or os.environ.get("HARNESS_CONTEXT_WINDOW_TOKENS")
        or ""
    ).strip()
    try:
        value = int(raw)
    except ValueError:
        return DEFAULT_CONTEXT_WINDOW_TOKENS
    return value if value > 0 else DEFAULT_CONTEXT_WINDOW_TOKENS


def latest_context_usage(session_id: str | None, context_window: int | None) -> dict[str, object] | None:
    if context_window is None or context_window <= 0:
        return None

    events = list(
        claude_usage.iter_usage_events(
            claude_usage.default_project_dir(),
            session_filter=session_id,
        )
    )
    if not events:
        return None

    event_session_id, _, _, usage, timestamp = max(events, key=lambda item: item[4] or "")
    used_tokens = usage.get("input_tokens")
    if not isinstance(used_tokens, int) or used_tokens < 0:
        return None

    return {
        "schema": "iao.context.v1",
        # CLAUDE_CODE_SESSION_ID is not consistently exported by Claude Code.
        # When absent, use the session id carried by the newest transcript event
        # instead of inventing a synthetic identifier.
        "sessionId": event_session_id,
        "contextWindowTokens": context_window,
        "contextUsedTokens": used_tokens,
        "source": "claude-transcript-adapter",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--session",
        default=os.environ.get("CLAUDE_CODE_SESSION_ID") or None,
        help="Optional Claude session filter; absent means newest local transcript event",
    )
    parser.add_argument(
        "--context-window",
        type=int,
        default=configured_context_window(),
        help="Context window in tokens (default: 200000; override with the Claude/common environment variable)",
    )
    args = parser.parse_args()
    usage = latest_context_usage(args.session, args.context_window)
    if usage is not None:
        print(json.dumps(usage, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
