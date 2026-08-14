---
name: spec-product
description: "draft the PRD proposal for a Specification run's product phase and satisfy PrdEvaluator"
---

# Draft the PRD

Write the product-phase proposal to `.harness/specification/active/prd.proposal.json` — a
real file, written with your file-write tool, in the exact shape the harness prompt shows you
(`schema`, `ideaDigest`, `vision`, `goals`, `successMetrics`, `nonGoals`, `scope`, `risks`,
`decisions`, `openQuestions`). Use `schema` exactly `"iao/prd/v1"` and set `ideaDigest` to the
accepted idea digest the prompt gives you — a remembered or approximate digest is rejected as
stale, since it is re-checked against the current accepted idea at evaluation time.

## What PrdEvaluator checks

- `schema` matches `"iao/prd/v1"` exactly, and `ideaDigest` matches the CURRENT accepted
  idea's digest.
- `goal`, `successMetric`, `risk` and `decision` ids are each unique within their own list.
- At least one entry each in `goals`, `nonGoals`, `scope` and `successMetrics`.
- Every success metric's `measure` and `target` are both present and are distinct strings —
  writing the same text (even case-insensitively) in both fields fails the check.
- No open question is left with `blocking: true` — resolve it or clear the flag before
  submitting; a PRD cannot be accepted with an unresolved blocking question.

## When done

Return `product` without arguments. The harness validates the file itself; on failure it
re-requests `product` with the exact violations to fix.
