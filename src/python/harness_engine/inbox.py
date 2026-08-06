"""File-based input channel — an alternative to argv for the turn's envelope.

The single-quoted argument transport (`./run-development.sh '<JSON>'`) has a
structural flaw: if the LLM driver forgets the closing quote, the shell enters
continuation mode and hangs BEFORE the process runs — no engine validation can catch it.
The inbox takes the payload out of shell quoting syntax: the agent writes the JSON here
with its file-write tool (it doesn't go through the shell) and runs the script WITH NO
arguments, a bare command that has no way of being left unterminated.
"""

from __future__ import annotations

import shutil
from pathlib import Path

from harness_engine import harness_log

_DIR = ".harness"
PATH = ".harness/inbox.json"

# Trail of the last consumed envelope — avoids reprocessing a stale JSON if the script
# runs twice without a rewrite, and doubles as a diagnostic.
CONSUMED_PATH = ".harness/inbox.consumed.json"


def read() -> str:
    """Raw inbox content, or "" if it doesn't exist. Parsing/sanitizing is the envelope's job."""
    try:
        p = Path(PATH)
        if p.exists():
            return p.read_text()
    except Exception as ex:
        harness_log.error(f"[Inbox] failed to read {PATH}: {ex}")

    return ""


def consume() -> None:
    """Moves the consumed inbox to CONSUMED_PATH after a successful parse."""
    try:
        p = Path(PATH)
        if p.exists():
            Path(_DIR).mkdir(parents=True, exist_ok=True)
            shutil.move(str(p), CONSUMED_PATH)
    except Exception as ex:
        harness_log.error(f"[Inbox] failed to consume {PATH}: {ex}")
