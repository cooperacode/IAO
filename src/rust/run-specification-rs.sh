#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"
for bin in "$DIR/.harness/bin/flows_specification" "$DIR/.harness/bin/flows_specification.exe"; do
  if [[ -f "$bin" && ! -d "$bin" ]]; then exec "$bin" "$@"; fi
done
echo "[harness] packaged Rust specification binary not found under $DIR/.harness/bin" >&2
exit 1
