#!/usr/bin/env bash
# Escafoldo pelo dev-initializer: deixa o workspace Rust pronto para build/test, do zero.
# Idempotente — pode ser rodado várias vezes sem quebrar.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

if ! command -v cargo >/dev/null 2>&1; then
  # shellcheck disable=SC1091
  [[ -f "$HOME/.cargo/env" ]] && source "$HOME/.cargo/env"
fi

command -v cargo >/dev/null 2>&1 || { echo "[init] cargo não encontrado — instale via https://rustup.rs" >&2; exit 1; }

cargo build --workspace
