#!/usr/bin/env bash
# Portão local/CI do harness: testes de unidade + golden set determinístico (0 tokens).
# Propaga o primeiro exit code != 0. Reusável por CI (ver .github/workflows/ci.yml) e à mão.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

echo "==> dotnet test (Harness.Engine.Tests)"
dotnet test src/dotnet/Harness.Engine.Tests/Harness.Engine.Tests.csproj -c Release

echo "==> smoke do fluxo de desenvolvimento (binário, ponta a ponta)"
# Dirige o binário do Flows.Development pela inbox num workspace descartável (não toca o
# .harness/ do repo). Pega o que o build sozinho não pega: transporte, inbox e serialização
# AOT em runtime. Determinístico e 0 tokens — o "driver" aqui é este script.
DEV_DLL="$DIR/src/dotnet/Flows.Development/bin/Release/net10.0/Flows.Development.dll"
[[ -f "$DEV_DLL" ]] || { echo "[smoke] DLL não encontrada: $DEV_DLL" >&2; exit 1; }

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
  ( cd "$SMOKE_DIR" && dotnet "$DEV_DLL" 2>/dev/null )
}

FEATURES='[{"id":1,"title":"A","priority":2},{"id":2,"title":"B","priority":1}]'
dev_step text start                                  >/dev/null
dev_step command plan "$FEATURES" "true" "app"       >/dev/null
LAST=""
for feature in 1 2; do
  dev_step command bearings  "orientado"  >/dev/null
  dev_step command smoke     "baseline ok" >/dev/null
  dev_step command pick                    >/dev/null
  dev_step command implement "feito"       >/dev/null
  dev_step command verify    "PASS"        >/dev/null
  LAST="$(dev_step command handoff "sha-$feature")"
done

[[ "$LAST" == "stop" ]] || { echo "[smoke] esperava 'stop' ao fim do loop, veio: '$LAST'" >&2; exit 1; }
grep -q '"passes":true' "$SMOKE_DIR/.harness/feature_list.json" \
  && ! grep -q '"passes":false' "$SMOKE_DIR/.harness/feature_list.json" \
  || { echo "[smoke] feature_list.json não fechou com todas passando" >&2; exit 1; }
echo "    loop fechou em stop e todas as features passam ✓"

echo "==> OK — testes verdes, golden set e smoke como esperado."
