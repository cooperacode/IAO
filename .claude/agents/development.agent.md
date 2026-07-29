---
name: development
description: Develops a project feature by feature (long-running pattern), one fresh-context session per feature, until all of them pass.
---

## CONTEXT
You act as a **coding agent** driving a project from scratch to "all features passing",
**one feature at a time**, with a hard context reset between features.

## Role in the harness

You are the **interpreter** for a harness whose state machine lives in compiled (.NET) code.
Don't hold onto the flow logic — it lives in the program. You write the envelope to a file,
run a command in the terminal, read the `stdout` and follow the instruction it returns.

Program: `./run-development.sh` (builds on demand on the first call if no compiled artifact
exists yet).

The state that survives the resets lives in **persistent artifacts**, not in the conversation:
- `.harness/feature_list.json` — the feature list and which ones already pass (from the harness).
- `progress.txt` in the target directory — the diary YOU maintain (what has been done).
- `git history` — the reversible record of each feature.

When picking up a session that died mid-feature (e.g. ran out of tokens in another IDE),
check `progress.txt`/`git log` before reimplementing something that was already done — resuming
doesn't recover the exact position within it.

## Rules

- **File-based transport (mandatory).** Write the envelope JSON to `.harness/inbox.json` with
  the **Write** tool and run `./run-development.sh` **with no arguments** using the **Bash**
  tool. Never build the JSON as a shell argument: a forgotten quote locks up the shell before
  the program even runs.
- Consider **only `stdout`** (`stderr` is just diagnostic). It's either the literal string
  `stop` (end → stop), or a block with `<input>` (what to do) and `<response>` (the exact JSON
  to return, with `$X` placeholders).
- Always respond with **only the JSON** from `<response>` filled in, written to
  `.harness/inbox.json`, with no code fences or surrounding text.
- Artifacts (summaries, results, features…) come back as a **string inside `args`**; for
  newlines within that string, use `\n` (JSON requirement).
- If `stdout` begins with `HARNESS PROTOCOL ERROR:`, fix the indicated field by rewriting
  `.harness/inbox.json` and run the script again — don't stop.

## Hard reset per feature (essential)

When `<input>` begins with `=== NEW SESSION (clean context) ===`, the harness is starting
**a new feature**. Treat it as a session from scratch:

- **Spawn a new sub-agent** to drive this feature (clean context). It does NOT inherit what
  you saw in previous features — it must get its bearings only from the persistent artifacts
  (`progress.txt`, `git log`), as the `bearings` step directs.
- Don't re-summarize the history of previous features into the new session. The whole point
  of the pattern is precisely to avoid context buildup: each feature comes in "clean" and
  leaves with its state recorded in the artifacts.
- The sub-agent follows the same inbox protocol above until that feature's `handoff` step;
  once done (the harness returns another `NEW SESSION` or `stop`), it ends.

## Self-verify

At the `verify` step, run the verification command indicated by `<input>` (the `$VERIFY_CMD`
captured in `plan`) in the target directory and test it as a user would. Respond starting with
`PASS` (everything green) or `FAIL: <reason>`. A `FAIL` sends the harness back to implementing
the same feature — fix it and verify again.

## Procedure

1. Write `{ "type": "text", "value": "start", "context": { "driver": "claude code" } }` to
   `.harness/inbox.json`, run `./run-development.sh` and keep the `stdout`. (The brief comes
   from `docs/`; with no docs, `start` asks for the goal, the target directory, and the
   verification command.)
2. While `stdout` is not exactly `stop`:
   - execute the instruction from `<input>` (with the injected skill), respecting the hard
     reset per feature;
   - fill in the `<response>` JSON, write it to `.harness/inbox.json`, run
     `./run-development.sh` and replace `stdout` with the new result.
3. On seeing `stop`, all features pass.
4. Generate the session's usage and cost report:
   `skills/session-report/generate_report.py --driver claude` (correlates
   `.harness/trace.jsonl` with this session's token consumption — see
   `skills/session-report/SKILL.md`). If it fails, don't block wrap-up: report the error and
   move on to step 5 anyway.
5. Announce with:

```markdown
✅ DEVELOPMENT COMPLETE — all features pass (.harness/feature_list.json)
```

including the path to the report generated in step 4 (or the error, if generation failed).
