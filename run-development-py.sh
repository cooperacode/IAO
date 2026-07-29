#!/usr/bin/env bash
# Stable invocation wrapper for the long-running development flow — Python port.
# start → plan → [bearings → smoke → pick → implement → verify → handoff]*
#
# No build step: points PYTHONPATH at src/python and runs the flows_development package
# via `python3 -m`. Requires only a Python 3.11+ interpreter installed — no SDK, no
# publish. Functional equivalent of run-development.sh (same JSON protocol, same
# paths under .harness/), useful where the .NET SDK isn't available.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$DIR/src/python"

PYTHON_BIN="${PYTHON_BIN:-python3}"
if ! command -v "$PYTHON_BIN" >/dev/null 2>&1; then
  echo "[harness] '$PYTHON_BIN' not found in PATH — install Python 3.11+ or set PYTHON_BIN." >&2
  exit 1
fi

PY_VERSION="$("$PYTHON_BIN" -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
PY_MAJOR="${PY_VERSION%%.*}"
PY_MINOR="${PY_VERSION##*.}"
if [[ "$PY_MAJOR" -lt 3 || ( "$PY_MAJOR" -eq 3 && "$PY_MINOR" -lt 11 ) ]]; then
  echo "[harness] Python $PY_VERSION found, but the minimum is 3.11." >&2
  exit 1
fi

export PYTHONPATH="$SRC${PYTHONPATH:+:$PYTHONPATH}"
exec "$PYTHON_BIN" -m flows_development "$@"
