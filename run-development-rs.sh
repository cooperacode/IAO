#!/usr/bin/env bash
# Wrapper de invocação estável do flow de desenvolvimento long-running — porta Rust
# (paralelo a run-development.sh e run-development-py.sh).
# start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
#
# Sem artefato publicado, builda o binário sob demanda na primeira chamada
# (cargo build --release). Diferente do .NET, não há distinção JIT/AOT: o binário do
# `cargo build --release` já é nativo.
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
    echo "[harness] cargo não encontrado — instale via https://rustup.rs" >&2
    exit 1
  }
  echo "[harness] nenhum artefato encontrado — buildando ($WORKSPACE)…" >&2
  (cd "$WORKSPACE" && cargo build --release --bin flows_development)
fi

exec "$BIN" "$@"
