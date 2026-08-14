---
name: spec-review
description: "assess readiness for a Specification run's review phase and satisfy ReadinessEvaluator"
---

# Assess readiness

Judge whether the accepted idea/PRD/SRS/SDD chain is internally consistent, or whether an
earlier phase needs rework. Write the review-phase proposal to
`.harness/specification/active/review.proposal.json` — a real file, written with your
file-write tool, in the exact shape the harness prompt shows you (`verdict`, `slices`,
`conflicts`, `residuals`). `conflicts` and `residuals` are always required arrays — use `[]`
when there are none, never omit them.

`verdict` must be exactly one of `"READY"`, `"FAIL:product"`, `"FAIL:analysis"` or
`"FAIL:design"`. A `FAIL:*` verdict rejects an earlier phase and must leave `slices` empty —
explain the rejection through `conflicts`/`residuals` instead of proposing slices.

## What ReadinessEvaluator checks (when verdict is READY)

- At least 1 and at most 10 readiness slices.
- Slice ids are unique.
- Every `requirementIds` entry resolves to a real requirement in the accepted SRS; every
  `adrIds` entry resolves to a real ADR in the accepted SDD.
- Every `dependsOn` entry names another slice in this same proposal — never itself, never an
  id outside the list.
- The `dependsOn` graph is acyclic, and at least one slice has an empty `dependsOn` (a
  starting slice with no prerequisite).
- Every requirement in the accepted SRS is covered by at least one slice's `requirementIds` —
  the slices must form a complete cover; nothing may be left untraced.

## Approval, downstream of a READY verdict

Once a run reaches `approve`, the proposal at
`.harness/specification/active/approval.proposal.json` needs `decision` exactly `"approved"`
or `"revise"`, `bundleDigest` matching the CURRENT bundle digest exactly (it is re-checked at
evaluation time, so resend with the freshly reported digest if any accepted document changed
since the preview), and a real, non-placeholder `rationale`. Publication itself re-runs the
readiness predicates above as part of its final safety gate before anything is written to
`specs/active/`.

## When done

Return `review` without arguments (or `approve` without arguments, for the approval step).
On `READY` the harness pauses for approval; on `FAIL:*` it recascades to the failing phase;
on a structural violation it re-requests the same command with the exact violations to fix.
