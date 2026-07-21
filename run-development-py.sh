#!/usr/bin/env bash
# Wrapper de invocação estável do flow de desenvolvimento long-running — porta Python.
# start → plan → [bearings → smoke → pick → implement → verify → handoff]*
#
# Sem etapa de build: aponta PYTHONPATH para src/python e roda o pacote flows_development
# via `python3 -m`. Requer apenas um interpretador Python 3.11+ instalado — sem SDK, sem
# publish. Equivalente funcional de run-development.sh (mesmo protocolo JSON, mesmos
# caminhos em .harness/), útil onde o .NET SDK não está disponível.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$DIR/src/python"

PYTHON_BIN="${PYTHON_BIN:-python3}"
if ! command -v "$PYTHON_BIN" >/dev/null 2>&1; then
  echo "[harness] '$PYTHON_BIN' não encontrado no PATH — instale Python 3.11+ ou defina PYTHON_BIN." >&2
  exit 1
fi

PY_VERSION="$("$PYTHON_BIN" -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
PY_MAJOR="${PY_VERSION%%.*}"
PY_MINOR="${PY_VERSION##*.}"
if [[ "$PY_MAJOR" -lt 3 || ( "$PY_MAJOR" -eq 3 && "$PY_MINOR" -lt 11 ) ]]; then
  echo "[harness] Python $PY_VERSION encontrado, mas o mínimo é 3.11." >&2
  exit 1
fi

export PYTHONPATH="$SRC${PYTHONPATH:+:$PYTHONPATH}"
exec "$PYTHON_BIN" -m flows_development "$@"
