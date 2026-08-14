---
name: spec-design
description: "draft the SDD proposal for a Specification run's design phase and satisfy SddEvaluator"
---

# Draft the SDD

Write the design-phase proposal to `.harness/specification/active/sdd.proposal.json` — a real
file, written with your file-write tool, in the exact shape the harness prompt shows you
(`schema`, `srsDigest`, `adrs`, `controls`). Use `schema` exactly `"iao/sdd/v1"` and set
`srsDigest` to the accepted SRS digest the prompt gives you.

## What SddEvaluator checks

- `schema` matches `"iao/sdd/v1"` exactly, and `srsDigest` matches the CURRENT accepted SRS's
  digest.
- ADR ids are unique — no two ADRs may share an id.
- Every functional and quality requirement from the accepted SRS is allocated to (referenced
  by) at least one ADR's `requirementIds` — no requirement may go undesigned.
- Every requirement id referenced from an ADR or a control must resolve to a real requirement
  in the accepted SRS — no dangling references.

## When done

Return `design` without arguments. The harness validates the file itself; on success it
persists `sdd.accepted.json` and the run pauses awaiting the next phase; on failure it
re-requests `design` with the exact violations to fix.
