#!/usr/bin/env bash
# Packaged invocation wrapper for the long-running development flow (Python).
# start → plan → [bearings → smoke → pick → implement → verify → handoff]*
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

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

[[ -d "$DIR/engine/flows_development" ]] || {
  echo "[harness] packaged Python engine not found under $DIR/engine" >&2
  exit 1
}

export PYTHONPATH="$DIR/engine${PYTHONPATH:+:$PYTHONPATH}"
exec "$PYTHON_BIN" -m flows_development "$@"
