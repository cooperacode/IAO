---
name: spec-discovery
description: "frame the idea proposal for a Specification run's discover phase and satisfy IdeaEvaluator"
---

# Frame the idea

If the harness prompt includes a `<sources>` block, it already read every file under the
sources folder for you (product docs, call transcripts, expert notes, regulations — whatever a
human curated there) — ground the idea in that material: cite and summarize it, don't invent
facts it doesn't support. If the prompt instead says no sources folder was found, ask the human
operator for the idea, problem, users and constraints in this conversation before proceeding;
do not fabricate a product from nothing just to have something to write.

Write the discover-phase proposal to `.harness/specification/active/idea.proposal.json` — a
real file, written with your file-write tool, in the exact shape the harness prompt shows you
(`schema`, `title`, `problem`, `users`, `desiredOutcomes`, `constraints`, `openQuestions`). Use
`schema` exactly `"iao/idea/v1"`.

## What IdeaEvaluator checks

- `schema` matches `"iao/idea/v1"` exactly.
- `title` and `problem` are both non-blank.
- At least one entry in `users` and one in `desiredOutcomes`.
- Every `openQuestions[].id` is unique — no two open questions may share an id.
- The canonical JSON stays under the configured UTF-8 byte ceiling (currently 20,000 bytes) —
  keep the idea focused; it frames a problem, it is not the full brief.
- The proposal's source and source digest are recorded — this provenance is populated by the
  harness around your proposal (the ingested sources folder's file list and content digest when
  one was found, or a fixed conversational tag otherwise), not a field you author yourself; do
  not omit or fabricate it.

Every violation is reported at once, each with a stable code (e.g. `IDEA_TITLE_MISSING`,
`IDEA_TOO_LARGE`, `IDEA_OPEN_QUESTION_DUPLICATE_ID`) — fix everything the harness reports
together rather than resubmitting one violation at a time.

## When done

Return `discover` without arguments. The harness validates the file itself; on failure it
re-requests `discover` with the exact violations to fix — do not guess at what might be wrong,
and never claim the file passed without the harness confirming it.
