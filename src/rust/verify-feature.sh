#!/usr/bin/env bash
# Escafoldo pelo dev-initializer: verifica uma feature do port Rust do harness.
# No começo roda a suíte completa (cargo test --workspace); pode ganhar filtro por feature
# mais adiante. Idempotente. Imprime PASS/FAIL e sai 0/não-zero de acordo.
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
  echo "PASS: feature ${FEATURE_ID} verificada"
  exit 0
else
  echo "FAIL: cargo test --workspace falhou para feature ${FEATURE_ID}"
  exit 1
fi
