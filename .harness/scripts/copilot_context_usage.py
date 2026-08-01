#!/usr/bin/env python3
"""Emit current GitHub Copilot Chat context usage in the common contract."""

from __future__ import annotations

import argparse
import json
import os

import copilot_usage


def latest_context_usage(session_id: str | None, context_window: int | None) -> dict[str, object] | None:
    if context_window is None or context_window <= 0:
        return None

    events = list(
        copilot_usage.iter_usage_events(
            copilot_usage.default_vscode_user_dir(),
            repo=copilot_usage.repo_root(),
            session_filter=session_id,
        )
    )
    if not events:
        return None

    event = max(events, key=lambda item: item[4] or "")
    event_session_id, _, _, usage, _, _, _ = event
    used_tokens = usage.get("input_tokens")
    if not isinstance(used_tokens, int) or used_tokens < 0:
        return None

    return {
        "schema": "iao.context.v1",
        "sessionId": event_session_id,
        "contextWindowTokens": context_window,
        "contextUsedTokens": used_tokens,
        "source": "copilot-transcript-adapter",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--session", default=os.environ.get("COPILOT_SESSION_ID"))
    parser.add_argument(
        "--context-window",
        type=int,
        default=int(
            os.environ.get("COPILOT_CONTEXT_WINDOW_TOKENS")
            or os.environ.get("HARNESS_CONTEXT_WINDOW_TOKENS")
            or 0
        ),
    )
    args = parser.parse_args()
    usage = latest_context_usage(args.session, args.context_window)
    if usage is not None:
        print(json.dumps(usage, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
