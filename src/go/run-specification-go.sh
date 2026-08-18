#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"
for bin in "$DIR/.harness/bin/flowsspecification" "$DIR/.harness/bin/flowsspecification.exe"; do
  if [[ -f "$bin" && ! -d "$bin" ]]; then exec "$bin" "$@"; fi
done
echo "[harness] packaged Go specification binary not found under $DIR/.harness/bin" >&2
exit 1
