#!/usr/bin/env bash
# Stable invocation wrapper for the long-running development flow — Go port
# (parallel to run-development.sh, run-development-py.sh and run-development-rs.sh).
# start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
#
# With no published artifact, builds the binary on demand on the first call
# (go build). As with Rust, there's no JIT/AOT distinction: the binary from `go build` is
# already native.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="$DIR/src/go"
BIN="$WORKSPACE/bin/flowsdevelopment"

command -v go >/dev/null 2>&1 || {
  echo "[harness] go not found — install via https://go.dev/dl/" >&2
  exit 1
}

if [[ ! -x "$BIN" ]]; then
  echo "[harness] no artifact found — building ($WORKSPACE)…" >&2
  ( cd "$WORKSPACE" && go build -o bin/flowsdevelopment ./flowsdevelopment )
fi

exec "$BIN" "$@"
