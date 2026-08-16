---
name: spec-design
description: "draft the SDD proposal for a Specification run's design phase and satisfy SddEvaluator"
---

# Draft the SDD

Write the design-phase proposal to `.harness/specification/active/sdd.proposal.json` — a real
file, written with your file-write tool, in the exact shape the harness prompt shows you
(`schema`, `srsDigest`, optional source provenance, `designContent`, `adrs`, `controls`). Use
`schema` exactly `"iao/sdd/v1"` and set `srsDigest` to the accepted SRS digest the prompt gives
you.

When the prompt supplies an accepted source bundle, copy its `sourceDigest` and `sourceFiles`
exactly and populate `designContent` with the detailed Markdown design. Preserve headings,
tables, Mermaid diagrams, fenced code blocks and folder/file trees instead of reducing them to
ADRs or controls. `designContent` must be a JSON string containing Markdown; do not put the
top-level `# Software Design Document` heading in it because the renderer supplies that title.
When no source bundle is supplied, `sourceDigest` and `sourceFiles` may be null, but retain any
detailed design in `designContent`.

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
