#!/usr/bin/env bash
# Scaffolded by dev-initializer: verifies a feature of the harness's Rust port.
# For now runs the full suite (cargo test --workspace); may get a per-feature filter
# later. Idempotent. Prints PASS/FAIL and exits 0/non-zero accordingly.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

FEATURE_ID="${1:-all}"

if ! command -v cargo >/dev/null 2>&1; then
  # shellcheck disable=SC1091
  [[ -f "$HOME/.cargo/env" ]] && source "$HOME/.cargo/env"
fi

./init.sh

if cargo test --workspace; then
  echo "PASS: feature ${FEATURE_ID} verified"
  exit 0
else
  echo "FAIL: cargo test --workspace failed for feature ${FEATURE_ID}"
  exit 1
fi
