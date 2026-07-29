#!/usr/bin/env bash
# Stable invocation wrapper for the long-running development flow — Rust port
# (parallel to run-development.sh and run-development-py.sh).
# start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
#
# With no published artifact, builds the binary on demand on the first call
# (cargo build --release). Unlike .NET, there's no JIT/AOT distinction: the binary from
# `cargo build --release` is already native.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="$DIR/src/rust"
BIN="$WORKSPACE/target/release/flows_development"

if ! command -v cargo >/dev/null 2>&1; then
  # shellcheck disable=SC1091
  [[ -f "$HOME/.cargo/env" ]] && source "$HOME/.cargo/env"
fi

if [[ ! -x "$BIN" ]]; then
  command -v cargo >/dev/null 2>&1 || {
    echo "[harness] cargo not found — install via https://rustup.rs" >&2
    exit 1
  }
  echo "[harness] no artifact found — building ($WORKSPACE)…" >&2
  (cd "$WORKSPACE" && cargo build --release --bin flows_development)
fi

exec "$BIN" "$@"
