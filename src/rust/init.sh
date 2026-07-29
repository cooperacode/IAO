#!/usr/bin/env bash
# Scaffolded by dev-initializer: gets the Rust workspace ready for build/test, from scratch.
# Idempotent — can be run multiple times without breaking.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

if ! command -v cargo >/dev/null 2>&1; then
  # shellcheck disable=SC1091
  [[ -f "$HOME/.cargo/env" ]] && source "$HOME/.cargo/env"
fi

command -v cargo >/dev/null 2>&1 || { echo "[init] cargo not found — install via https://rustup.rs" >&2; exit 1; }

cargo build --workspace
