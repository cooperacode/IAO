#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"
PYTHON_BIN="${PYTHON_BIN:-python3}"
exec "$PYTHON_BIN" -m flows_specification "$@"
