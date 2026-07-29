---
name: dev-bearings
description: "get your bearings at the start of a fresh-context session"
---

# SKILL: get bearings

You start a **fresh** session, with no memory of previous ones. Before touching any code,
rebuild context from the persistent artifacts — only they can be trusted. If the prompt
includes a `<brief>` block, that's the project's original brief, persisted by the harness —
reread it so you don't lose sight of motivation, constraints, or requirements that never became
their own feature; it doesn't replace `progress.txt`/`git log` for knowing where the work left
off.

- `pwd` and list only the top of the target directory to know where you are.
- Read only the tail of `progress.txt` (e.g. `tail -n 20 progress.txt`): each line carries a
  UTC timestamp in brackets — it's the lightweight separator between sessions. Use it to
  quickly spot the most recent entry without dumping the whole history into context.
- Run `git log --oneline -10`: the recent history confirms what the commits recorded.
- Don't open full logs by default. If you need to investigate a log under `.harness/logs/`,
  read only a small excerpt first (`tail -n 80`, search for the error, a specific file).

If `progress.txt` doesn't exist yet (the harness's first feature running in this directory —
common for a brownfield app the harness has never touched before), create it now with an
initial context line, and don't rely on the project's general `git log -15` to get your
bearings: in a large, pre-existing repository, recent commits may belong to another
team/period and say nothing about the change underway. In that case, get your bearings from
the `<brief>` (when present) and the actual state of the code relevant to the current feature,
not the whole project's history.

Don't trust assumptions about the state — verify it. Summarize in `$NOTE`, in 2-4 lines, what
you found and where the work left off. Don't paste logs, diffs, or long listings into `$NOTE`.
