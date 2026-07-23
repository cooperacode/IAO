#!/usr/bin/env bash
# Portão local/CI da porta Python do harness: testes pytest + smoke E2E determinístico
# (0 tokens). Propaga o primeiro exit code != 0. Espelha run-checks.sh (lado .NET).
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

PYTHON_BIN="${PYTHON_BIN:-python3}"

echo "==> pytest (src/python/tests)"
PYTHONPATH="$DIR/src/python" "$PYTHON_BIN" -m pytest "$DIR/src/python/tests" -q

echo "==> smoke do fluxo de desenvolvimento (Python, ponta a ponta)"
# Dirige o wrapper Python pela inbox num workspace descartável (não toca o .harness/ do
# repo). Pega o que o teste unitário não pega: transporte, inbox e o processo CLI real.
# Determinístico e 0 tokens — o "driver" aqui é este script.
WRAPPER="$DIR/run-development-py.sh"
[[ -x "$WRAPPER" ]] || { echo "[smoke] wrapper não encontrado ou não executável: $WRAPPER" >&2; exit 1; }

SMOKE_DIR="$(mktemp -d)"
trap 'rm -rf "$SMOKE_DIR"' EXIT
mkdir -p "$SMOKE_DIR/.harness"

json_escape() { local s=$1; s=${s//\\/\\\\}; s=${s//\"/\\\"}; printf '%s' "$s"; }

dev_step() {  # tipo valor [args...] → escreve a inbox (JSON) e roda um passo; ecoa o stdout
  local typ="$1" val="$2"; shift 2
  local json="{\"type\":\"$typ\",\"value\":\"$val\"" first=1 a
  if [[ $# -gt 0 ]]; then
    json+=',"args":['
    for a in "$@"; do
      [[ $first -eq 1 ]] || json+=','
      json+="\"$(json_escape "$a")\""; first=0
    done
    json+=']'
  fi
  json+='}'
  printf '%s' "$json" > "$SMOKE_DIR/.harness/inbox.json"
  ( cd "$SMOKE_DIR" && "$WRAPPER" 2>/dev/null )
}

FEATURES='[{"id":1,"title":"A","priority":2},{"id":2,"title":"B","priority":1}]'
dev_step text start                                  >/dev/null
dev_step command plan "$FEATURES" "true" "app"       >/dev/null
mkdir -p "$SMOKE_DIR/app"
cat > "$SMOKE_DIR/app/init.sh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
SH
chmod +x "$SMOKE_DIR/app/init.sh"
cat > "$SMOKE_DIR/app/verify-feature.sh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
./init.sh
true
echo "PASS: feature ${1:-all} verificada"
SH
chmod +x "$SMOKE_DIR/app/verify-feature.sh"
LAST=""
for feature in 1 2; do
  dev_step command bearings  "orientado"  >/dev/null
  dev_step command smoke     "baseline ok" >/dev/null
  dev_step command pick                    >/dev/null
  LAST="$(dev_step command implement "feito")"
done

[[ "$LAST" == "stop" ]] || { echo "[smoke] esperava 'stop' ao fim do loop, veio: '$LAST'" >&2; exit 1; }
grep -q '"passes":true' "$SMOKE_DIR/.harness/feature_list.json" \
  && ! grep -q '"passes":false' "$SMOKE_DIR/.harness/feature_list.json" \
  || { echo "[smoke] feature_list.json não fechou com todas passando" >&2; exit 1; }
[[ -s "$SMOKE_DIR/.harness/logs/verify-feature-2.log" ]] \
  || { echo "[smoke] log de verify-feature não foi criado" >&2; exit 1; }
echo "    loop fechou em stop e todas as features passam ✓"

echo "==> OK — testes verdes e smoke como esperado."
