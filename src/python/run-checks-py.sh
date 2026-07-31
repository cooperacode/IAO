#!/usr/bin/env bash
# Local/CI gate for the Python port of the harness: pytest tests + deterministic E2E smoke
# (0 tokens). Propagates the first non-zero exit code. Mirrors run-checks.sh (.NET side).
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

PYTHON_BIN="${PYTHON_BIN:-python3}"

echo "==> pytest (src/python/tests)"
PYTHONPATH="$DIR" "$PYTHON_BIN" -m pytest "$DIR/tests" -q

echo "==> development flow smoke test (Python, end to end)"
# Drives the Python engine through the inbox in a disposable workspace (doesn't touch the
# repo's .harness/). Catches what the unit test doesn't: transport, inbox and the real CLI
# process. Deterministic and 0 tokens — the "driver" here is this script.
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
  ( cd "$SMOKE_DIR" && PYTHONPATH="$DIR" "$PYTHON_BIN" -m flows_development 2>/dev/null )
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

echo "==> OK — tests green and smoke as expected."
