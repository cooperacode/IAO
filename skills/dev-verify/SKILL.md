---
name: dev-verify
description: "E2E self-verify of the feature as a user would"
---

# SKILL: E2E self-verify

Verify the feature **as a user would**, not just that the code compiles. The goal is to prove
the behavior end-to-end before declaring the feature done.

- If there's a `verify-feature.sh` in the target directory, run
  `./verify-feature.sh <feature-id>`; it's the harness's preferred wrapper and may run the full
  suite. When running it manually, redirect full output to `.harness/logs/verify-<id>.log`.
- If there's no wrapper, run the project's verification command (`$VERIFY_CMD`) in the target
  directory and observe the actual result — don't assume it passed. Also redirect full output
  to `.harness/logs/verify-<id>.log`.
- Read only the necessary excerpt of the log into context (`tail -n 80`, first failure,
  relevant stack trace). Don't paste whole logs.
- When it makes sense, exercise the actual user path (the route, the screen, the call), not
  just the unit test.
- Be honest: a false "passed" only pushes the problem to the next session, which starts
  without your context and will have a harder time finding the cause.

Answer in `$RESULT` starting with:
- `PASS: <short summary>. Log: <path>` — everything green, behavior confirmed; or
- `FAIL: <short reason>. Log: <path>` — what failed (this becomes the hook for the fix).
