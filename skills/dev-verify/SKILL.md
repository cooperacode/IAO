---
name: dev-verify
description: "repair the deterministic feature verifier when the harness cannot execute it"
---

# Repair deterministic verification

The harness could not execute its verifier. Repair the verification setup; the harness will
rerun it and decide from the process result.

- Inspect the reported failure and the relevant verifier log excerpt.
- Prefer repairing `verify-feature.sh <feature-id>`; otherwise repair the configured
  verification command or target path.
- Preserve the project's established verification convention. Do not replace it with a
  weaker compile-only check.
- Run the repaired verifier locally when possible, keeping full output under `.harness/logs/`.
- Do not claim `PASS` or `FAIL` as evidence. The harness accepts only its own deterministic
  execution result.
