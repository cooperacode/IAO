---
name: dev-handoff
description: "leave clean state for the next session"
---

# SKILL: leave clean state

The next session starts **without your context** — it will only have what you leave on
record. A clean handoff is what makes the loop resumable.

## 1. Descriptive commit
`git commit` with a message that explains **what** and **why**, referencing the feature.
A clean working tree is the guarantee that the next `git log`/bisect makes sense. Don't leave
uncommitted changes or temporary files.

Don't include full logs in the commit. The harness's operational logs belong in
`.harness/logs/`, which is local state ignored by Git; cite the path in `progress.txt` when it
explains how to investigate a failure/validation.

## 2. Update progress
Append **a single physical line** to `progress.txt` with: the completed feature (id + title),
what was done, and how to verify it. This is what the next session reads in `bearings` to get
its bearings — a `tail -n 20` only works as a quick summary if each entry fits on one line.

Before writing, actually run `date -u +"%Y-%m-%d %H:%M UTC"` in the shell and use the literal
output as the bracketed prefix — never write `UTC` without a time (this breaks the timestamp
parsing and the purpose of the prefix as a separator between sessions):
`[2026-07-21 20:18 UTC] Feature #8 - Filter listing...: <what was done>. Verify with: ...`

Never split the entry into paragraphs/multi-line blocks, no matter how much detail there is
about what was done — summarize it. If you feel the need for more detail than fits on one
line, that detail belongs in `.harness/logs/` (cited by path), not in `progress.txt`.

Before appending, check the last lines of `progress.txt`: if the harness's automatic handoff
already logged an entry `[date HH:MM UTC] Feature #<id> - ...` for this same feature, don't
create a second one — that duplicates the record in two different formats for the same work.
This manual step is meant to fill the gap when the automatic handoff failed or didn't run, not
to supplement an entry that already exists.

Write it so that someone without your context understands in 10 seconds where the work
stands.

Confirm with the commit hash in `$COMMIT`.
