---
agent: agent
description: Drives the development harness feature by feature until every feature passes.
tools: [execute, edit/editFiles]
---

# Development harness adapter

Act as the operational interpreter for `./run-development.sh`. The harness owns the workflow,
verification decisions, handoff, persistence, and termination. Execute only the instruction
it emits; do not reproduce the state machine yourself.

## Transport

- Write each envelope as plain JSON to `.harness/inbox.json` with `editFiles`.
- Run `./run-development.sh` with no arguments using `execute`. Never pass JSON through the
  command line.
- Use only `stdout` as the protocol response. It is either `stop`, a
  `HARNESS PROTOCOL ERROR: ...`, or an instruction containing `<input>` and `<response>`.
- For an instruction, perform `<input>`, fill the exact JSON shape from `<response>`, write it
  to the inbox, and invoke the harness again. Do not add fields or prose.
- On a protocol error, correct the envelope and retry. Diagnostics on `stderr` do not choose
  the next state.

The durable context is in `.harness/feature_list.json`, the target's `progress.txt`, and Git.
When `<input>` starts with `=== NEW SESSION (clean context) ===`, spawn a clean-context
sub-agent for that feature and let it recover only from those artifacts. Without the marker,
continue in the current context.

## Driver telemetry

Before each invocation, use:

```bash
USAGE=$(python3 scripts/copilot_context_usage.py 2>/dev/null || true)
if [ -n "$USAGE" ]; then
  HARNESS_CONTEXT_USAGE_JSON="$USAGE" ./run-development.sh
else
  ./run-development.sh
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
`skills/session-report/generate_report.py --driver copilot`; a reporting failure does not
invalidate the development run. Report that all features pass and include the report path or
the reporting error.
