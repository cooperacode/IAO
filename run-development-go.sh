#!/usr/bin/env bash
# Wrapper de invocação estável do flow de desenvolvimento long-running — porta Go
# (paralelo a run-development.sh, run-development-py.sh e run-development-rs.sh).
# start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
#
# Sem artefato publicado, builda o binário sob demanda na primeira chamada
# (go build). Como no Rust, não há distinção JIT/AOT: o binário do `go build` já é nativo.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="$DIR/src/go"
BIN="$WORKSPACE/bin/flowsdevelopment"

command -v go >/dev/null 2>&1 || {
  echo "[harness] go não encontrado — instale via https://go.dev/dl/" >&2
  exit 1
}

if [[ ! -x "$BIN" ]]; then
  echo "[harness] nenhum artefato encontrado — buildando ($WORKSPACE)…" >&2
  ( cd "$WORKSPACE" && go build -o bin/flowsdevelopment ./flowsdevelopment )
fi

exec "$BIN" "$@"
