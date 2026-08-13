#!/usr/bin/env bash
# Packaged invocation wrapper for the Specification flow (.NET).
# start → discover → product → analysis → design → review → approve → stop
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

for bin in "$DIR/.harness/bin/Flows.Specification" "$DIR/.harness/bin/Flows.Specification.exe"; do
  if [[ -f "$bin" && ! -d "$bin" ]]; then
    exec "$bin" "$@"
  fi
done

echo "[harness] packaged .NET binary not found under $DIR/.harness/bin" >&2
exit 1
