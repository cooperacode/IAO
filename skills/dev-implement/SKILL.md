---
name: dev-implement
description: "implement exactly one feature, incrementally"
---

# SKILL: implement ONE feature

Implement **only** the indicated feature — nothing else. The one-at-a-time discipline is what
keeps each session small enough to fit in a fresh context and each commit reversible. If the
prompt includes a `<brief>` block, use it only to understand the motivation/constraints behind
this feature — it's not an invitation to get ahead on others.

- Read the feature's `Description` and `Brief references` (when present) before coding — they
  summarize what this feature needs to deliver and, if any, the brief codes (e.g. "RF-003") it
  implements; they're not an invitation to revisit the whole brief or expand the scope beyond
  what the description already covers.
- Make the smallest change that delivers the complete feature. No "while I'm at it".
- Don't get ahead on other features, even if they look trivial — each one gets its turn.
- Leave the code building at the end; a broken intermediate step gets in the way of
  self-verify. If you run commands with long output, record the detail in `.harness/logs/` and
  read only the relevant excerpt to decide the next adjustment.
- If you discover the feature depends on something not yet done, prefer the minimum needed to
  unblock it and note the dependency in the summary — don't expand the scope.

Summarize in `$SUMMARY` what changed (files and behavior), objective and short. Don't paste
logs, full diffs, or test output into the summary; cite the log path when it's useful.
