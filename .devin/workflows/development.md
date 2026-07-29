---
description: Develops a project feature by feature (long-running pattern) — the initializer expands the brief into prioritized features; a loop of fresh-context sessions implements them one at a time (bearings, smoke, pick, implement, verify, handoff) until all pass — driving the .NET Flows.Development harness.
auto_execution_mode: 3
---

# Long-running development

Takes a project from scratch to "all features passing", **one feature at a time**, with a
hard context reset between features.

You are the **interpreter** for a harness whose state machine lives in compiled (.NET) code.
**The flow logic lives in the program, not with you.** At each step you write the envelope to
a file, run a command, read the `stdout` and follow exactly the instruction it returns. Don't
decide the next step on your own.

Program: `./run-development.sh`

## Channel contract

- **`stdout`** = the next instruction. It's the literal string `stop` (end → stop), or a
  block with `<input>` (what to do) and `<response>` (the exact JSON to return, with `$X`
  placeholders, and possibly a `<skills>` with the step's skill).
- **`stderr`** = diagnostic. Ignore it when deciding the next step.
- **Your output at each step** = only the JSON from `<response>` filled in, written to
  `.harness/inbox.json`, with no code fences or surrounding text.

## Rules

1. **File-based transport (mandatory).** Write the envelope JSON to `.harness/inbox.json` and
   run `./run-development.sh` **with no arguments**. Never build the JSON as a shell argument:
   a forgotten quote locks up the shell before the program even runs.
2. Base your decision **only on `stdout`**.
3. Artifacts (summaries, results, features…) come back as a **string inside `args`**; for
   newlines, use `\n` (JSON requirement).
4. If `stdout` begins with `HARNESS PROTOCOL ERROR:`, fix the indicated field by rewriting
   `.harness/inbox.json` and **run the script again** — don't stop.

## Hard reset per feature (essential)

When `<input>` begins with `=== NEW SESSION (clean context) ===`, the harness is starting
**a new feature**. Treat it as a session from scratch: **spawn a new sub-agent** to drive it,
which gets its bearings **only** from the persistent artifacts (`progress.txt`, `git log`) —
don't inherit or re-summarize the context from previous features.

When picking up a session that died mid-feature (e.g. ran out of tokens in another IDE),
check `progress.txt`/`git log` before reimplementing something that was already done —
resuming doesn't recover the exact position within it.

## Self-verify

At the `verify` step, run the verification command indicated in `<input>` (`$VERIFY_CMD`
captured in `plan`) in the target directory and test it as a user would. Respond starting with
`PASS` or `FAIL: <reason>`. A `FAIL` sends the harness back to implementing the same feature.

## Steps

1. Start the flow: write `{ "type": "text", "value": "start", "context": { "driver": "devin" } }`
   to `.harness/inbox.json`, run the script with no arguments and keep the `stdout`:
   ```bash
   ./run-development.sh
   ```
   (With no compiled artifact yet, the script builds on demand on the first call.)

2. While `stdout` is **not** exactly `stop`:
   - Execute the instruction from the `<input>` block (with the skill from the `<skills>`
     block), respecting the hard reset per feature.
   - Fill in the JSON from the `<response>` block, write it to `.harness/inbox.json` and run
     `./run-development.sh` (no arguments).
   - Replace `stdout` with the new result and repeat.

3. On seeing `stop`, all features pass (`.harness/feature_list.json`). Report:

```markdown
✅ DEVELOPMENT COMPLETE — all features pass (.harness/feature_list.json)
```

> No usage/cost report for Devin: `skills/session-report/` depends on a script
> `scripts/devin_usage.py` (equivalent to `claude_usage.py`/`codex_usage.py`/
> `copilot_usage.py`) that doesn't exist yet — don't skip this note thinking it's just a matter
> of calling the skill with `--driver devin`, it will fail (`choices` only accepts
> `claude`/`codex`/`copilot`). Don't generate the report in this workflow until that script
> exists.
