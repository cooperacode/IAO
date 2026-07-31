#!/usr/bin/env bash
# Packaged invocation wrapper for the long-running development flow (.NET).
# start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

for bin in "./bin/Flows.Development" "./bin/Flows.Development.exe"; do
  if [[ -f "$bin" && ! -d "$bin" ]]; then
    exec "$bin" "$@"
  fi
done

echo "[harness] packaged .NET binary not found under $DIR/bin" >&2
exit 1
