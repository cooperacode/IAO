---
agent: agent
description: Drives the Specification harness from idea through publish.
tools: [execute, edit/editFiles]
---

# Specification harness adapter

Act as the operational interpreter for `./run-specification.sh`. The harness owns the state
machine, evaluation, digest chaining, and publication. Execute only the instruction it emits;
do not reproduce the state machine yourself.

## Transport

- Write each envelope as plain JSON to `.harness/inbox.json` with `editFiles`.
- Run `./run-specification.sh` with no arguments using `execute`. Never pass JSON through the
  command line.
- Use only `stdout` as the protocol response. It is either `stop`, a
  `HARNESS PROTOCOL ERROR: ...`, or an instruction containing `<input>` and `<response>`.
- For an instruction, perform `<input>`, fill the exact JSON shape from `<response>`, write it
  to the inbox, and invoke the harness again. Do not add fields or prose.
- When `<input>` asks you to write a proposal file (e.g.
  `.harness/specification/active/idea.proposal.json`), write that file with `editFiles` too —
  as its own real file, in raw form, never escaped as a string value inside the inbox envelope.
  If a `<sources>` block is present, ground the proposal in it instead of inventing content. If
  you cannot produce a valid proposal, do not fabricate a placeholder; retry the step instead.
- On a protocol error, correct the envelope and retry. Diagnostics on `stderr` do not choose
  the next state.

Unlike the Development harness, no session ever needs to be reset here — every phase
(`discover`, `product`, `analysis`, `design`, `review`, `approve`) is meant to run inside this
same continuous session, and no instruction from this harness ever carries a
`=== NEW SESSION (clean context) ===` marker. The durable context is
`.harness/specification/active/` (run state, proposals, accepted documents) and, once
published, `specs/active/`.

## Driver telemetry

Before each invocation, use:

```bash
USAGE=$(python3 .harness/scripts/copilot_context_usage.py 2>/dev/null || true)
if [ -n "$USAGE" ]; then
  HARNESS_CONTEXT_USAGE_JSON="$USAGE" ./run-specification.sh
else
  ./run-specification.sh
fi
```

Set `COPILOT_CONTEXT_WINDOW_TOKENS` or `HARNESS_CONTEXT_WINDOW_TOKENS` only when the host
provides the active context window.

## Run

Start by writing:

```json
{"type":"text","value":"start","context":{"driver":"github copilot"}}
```

Continue until `stdout` is exactly `stop`. Then run
`.harness/skills/session-report/generate_report.py --driver copilot`; a reporting failure does not
invalidate the run. Report the run's final status (`completed`, `awaiting_approval`,
`needs_human_decision`, or `publish_blocked` — see `.harness/specification/active/run.json`) and
include the report path or the reporting error.
