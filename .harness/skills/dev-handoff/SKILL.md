---
name: dev-handoff
description: "repair repository or progress state after the harness's automatic handoff fails"
---

# Repair automatic handoff

The harness already verified the feature but could not finish its deterministic progress/Git
handoff. Repair only the reported obstacle so it can retry safely.

- Inspect the reported handoff failure, `git status --short`, and the tail of `progress.txt`.
- Resolve repository initialization, Git identity, permissions, staging, commit, or dirty-tree
  problems as narrowly as possible.
- Keep `.harness/` logs and state out of the commit.
- If a progress entry already exists for this feature and result, do not append another.
- If manual progress repair is necessary, keep the entry to one physical line using
  `[YYYY-MM-DD HH:MM UTC] Feature #<id> - <title>: ...`.
- Leave the target clean enough for the harness to retry. Do not mark the feature passing or
  offer a commit hash as evidence; the harness inspects the repository itself.
