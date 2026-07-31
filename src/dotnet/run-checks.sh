#!/usr/bin/env bash
# Local/CI gate for the harness: unit tests + deterministic golden set (0 tokens).
# Propagates the first non-zero exit code. Reusable by CI (see .github/workflows/ci.yml) and by hand.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

echo "==> dotnet test (Harness.Engine.Tests)"
dotnet test Harness.Engine.Tests/Harness.Engine.Tests.csproj -c Release

echo "==> development flow smoke test (binary, end to end)"
# Drives the Flows.Development binary through the inbox in a disposable workspace (doesn't
# touch the repo's .harness/). Catches what the build alone doesn't: transport, inbox and
# AOT serialization at runtime. Deterministic and 0 tokens — the "driver" here is this script.
DEV_DLL="$DIR/Flows.Development/bin/Release/net10.0/Flows.Development.dll"
[[ -f "$DEV_DLL" ]] || { echo "[smoke] DLL not found: $DEV_DLL" >&2; exit 1; }

SMOKE_DIR="$(mktemp -d)"
trap 'rm -rf "$SMOKE_DIR"' EXIT
mkdir -p "$SMOKE_DIR/.harness"

json_escape() { local s=$1; s=${s//\\/\\\\}; s=${s//\"/\\\"}; printf '%s' "$s"; }

dev_step() {  # type value [args...] → writes the inbox (JSON) and runs one step; echoes stdout
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
echo "PASS: feature ${1:-all} verified"
SH
chmod +x "$SMOKE_DIR/app/verify-feature.sh"
LAST=""
for feature in 1 2; do
  dev_step command bearings  "oriented"  >/dev/null
  dev_step command smoke     "baseline ok" >/dev/null
  dev_step command pick                    >/dev/null
  LAST="$(dev_step command implement "done")"
done

[[ "$LAST" == "stop" ]] || { echo "[smoke] expected 'stop' at the end of the loop, got: '$LAST'" >&2; exit 1; }
grep -Eq '"passes"[[:space:]]*:[[:space:]]*true' "$SMOKE_DIR/.harness/feature_list.json" \
  && ! grep -Eq '"passes"[[:space:]]*:[[:space:]]*false' "$SMOKE_DIR/.harness/feature_list.json" \
  || { echo "[smoke] feature_list.json did not close with all features passing" >&2; exit 1; }
[[ -s "$SMOKE_DIR/.harness/logs/verify-feature-2.log" ]] \
  || { echo "[smoke] verify-feature log was not created" >&2; exit 1; }
echo "    loop closed on stop and all features pass ✓"

echo "==> OK — tests green, golden set and smoke as expected."
