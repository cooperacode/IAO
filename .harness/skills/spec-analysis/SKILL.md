---
name: spec-analysis
description: "draft the SRS proposal for a Specification run's analysis phase and satisfy SrsEvaluator"
---

# Draft the SRS

Write the analysis-phase proposal to `.harness/specification/active/srs.proposal.json` — a
real file, written with your file-write tool, in the exact shape the harness prompt shows you
(`schema`, `prdDigest`, `functionalRequirements`, `qualityRequirements`, `acceptanceCriteria`,
`interfaces`, `dataRules`, `delivery`). Use `schema` exactly `"iao/srs/v1"` and set `prdDigest`
to the accepted PRD digest the prompt gives you.

## What SrsEvaluator checks

- `schema` matches `"iao/srs/v1"` exactly, and `prdDigest` matches the CURRENT accepted PRD's
  digest.
- Requirement ids are unique across `functionalRequirements` and `qualityRequirements`
  combined — an id may not be reused between the two lists.
- Every goal id from the accepted PRD is covered by at least one requirement's `goalIds` — no
  PRD goal may go untraced into a requirement.
- Every requirement has at least one `acceptanceIds` entry, and every id it lists must resolve
  to a real entry in `acceptanceCriteria`.
- Every requirement object must include non-null arrays for `goalIds`, `dependsOn`, and
  `acceptanceIds`. Use an empty array when there are no dependencies; do not omit the fields.
- Every `dependsOn` id on a requirement must resolve to another requirement that actually
  exists in this same proposal.
- Every requirement id referenced from an acceptance criterion, an interface, or a data rule
  must resolve to a real requirement — no dangling references anywhere in the document.

## When done

Return `analysis` without arguments. The harness validates the file itself; on failure it
re-requests `analysis` with the exact violations to fix.
