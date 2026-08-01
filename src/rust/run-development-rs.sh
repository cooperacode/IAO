#!/usr/bin/env bash
# Packaged invocation wrapper for the long-running development flow (Rust).
# start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

for bin in "$DIR/.harness/bin/flows_development" "$DIR/.harness/bin/flows_development.exe"; do
  if [[ -f "$bin" && ! -d "$bin" ]]; then
    exec "$bin" "$@"
  fi
done

echo "[harness] packaged Rust binary not found under $DIR/.harness/bin" >&2
exit 1
