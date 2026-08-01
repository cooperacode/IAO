#!/usr/bin/env bash
# Static guard for the model-facing development contract. Keeps skills, adapters, and the
# four engine ports from drifting back to model-authored verification/handoff artifacts.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

fail() {
  echo "[development-contract] $*" >&2
  exit 1
}

skills=(
  skills/dev-initializer/SKILL.md
  skills/dev-implement/SKILL.md
  skills/dev-smoke/SKILL.md
  skills/dev-verify/SKILL.md
  skills/dev-handoff/SKILL.md
)
adapters=(
  .claude/agents/development.agent.md
  .codex/agents/development.toml
  .github/prompts/development.prompt.md
  .devin/workflows/development.md
)
prompts=(
  src/dotnet/Flows.Development/DevelopmentTasks.Prompt.cs
  src/python/flows_development/prompts.py
  src/go/flowsdevelopment/prompts.go
  src/rust/flows_development/src/prompts.rs
)

for file in "${skills[@]}" "${adapters[@]}" "${prompts[@]}"; do
  [[ -f "$file" ]] || fail "required contract file missing: $file"
done
[[ ! -e skills/dev-bearings/SKILL.md ]] \
  || fail "dev-bearings is obsolete: bearings is deterministic harness work"

retired_tokens=('$NOTE' '$SMOKE' '$SUMMARY' '$RESULT' '$COMMIT')
for token in "${retired_tokens[@]}"; do
  if grep -Fq "$token" "${skills[@]}" "${prompts[@]}"; then
    fail "retired model-authored token still present: $token"
  fi
done

if grep -Eiq 'self-verify|respond starting with.*PASS|answer in.*PASS' "${adapters[@]}"; then
  fail "an adapter still asks the model to self-attest verification"
fi

for adapter in "${adapters[@]}"; do
  for required in '.harness/inbox.json' './run-development.sh' '<input>' '<response>' \
    '=== NEW SESSION (clean context) ===' 'exactly `stop`'; do
    grep -Fq "$required" "$adapter" \
      || fail "$adapter is missing protocol invariant: $required"
  done
done

dotnet_prompt="${prompts[0]}"
python_prompt="${prompts[1]}"
go_prompt="${prompts[2]}"
rust_prompt="${prompts[3]}"

[[ "$(grep -Fc 'new Envelope(EnvelopeType.Command, "verify", [])' "$dotnet_prompt")" -ge 2 ]] \
  || fail ".NET verify repair prompts must return verify"
[[ "$(grep -Fc 'Envelope(EnvelopeType.COMMAND, "verify", ())' "$python_prompt")" -ge 2 ]] \
  || fail "Python verify repair prompts must return verify"
[[ "$(grep -Fc 'engine.NewEnvelope(engine.EnvelopeType.Command, "verify", []string{})' "$go_prompt")" -ge 2 ]] \
  || fail "Go verify repair prompts must return verify"
[[ "$(grep -Fc 'Envelope::new(envelope_type::COMMAND, "verify", vec![])' "$rust_prompt")" -ge 2 ]] \
  || fail "Rust verify repair prompts must return verify"

grep -Fq '<bearings>' "$dotnet_prompt" || fail ".NET implement prompt must include bearings"
grep -Fq '<bearings>' "$python_prompt" || fail "Python implement prompt must include bearings"
grep -Fq '<bearings>' "$go_prompt" || fail "Go implement prompt must include bearings"
grep -Fq '<bearings>' "$rust_prompt" || fail "Rust implement prompt must include bearings"

if grep -Eq 'HandoffRetryPrompt|handoff_retry_prompt' \
  src/dotnet/Flows.Development/DevelopmentTasks.cs \
  src/dotnet/Flows.Development/DevelopmentTasks.Prompt.cs \
  src/python/flows_development/tasks.py \
  src/python/flows_development/prompts.py \
  src/go/flowsdevelopment/tasks.go \
  src/go/flowsdevelopment/prompts.go \
  src/rust/flows_development/src/tasks.rs \
  src/rust/flows_development/src/prompts.rs; then
  fail "handoff without deterministic PASS must return to verify, not retry handoff"
fi

echo "[development-contract] skills, adapters, and engine prompts are aligned"
