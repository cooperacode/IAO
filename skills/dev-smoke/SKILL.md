---
name: dev-smoke
description: "smoke test the baseline before implementing"
---

# SKILL: smoke test

Before touching any feature, confirm the baseline is healthy — that way, if something breaks
later, you know it was your change and not an inherited broken state.

- Run `./init.sh` in the target directory with full output redirected to
  `.harness/logs/smoke.log` (create the folder if needed).
- Verify the app comes up/builds without error (the bare minimum that should work, works).
- If it fails, read only the relevant excerpt of the log (e.g. the first failure, `tail -n 80`,
  the file cited by the stack trace). Don't paste the whole log into context.

If the smoke test **fails**, the baseline is broken: fix that first — don't stack a new
feature on unstable ground. Report the result in `$SMOKE` as `ok` or
`FAIL: <main error>. Log: <path>`.
