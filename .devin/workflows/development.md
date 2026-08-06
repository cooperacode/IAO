---
description: Drives the development harness feature by feature until every feature passes.
auto_execution_mode: 3
---

# Development harness adapter

Act as the operational interpreter for `./run-development.sh`. The harness owns the workflow,
verification decisions, handoff, persistence, and termination. Execute only the instruction
it emits; do not reproduce the state machine yourself.

## Transport

- Write each envelope as plain JSON to `.harness/inbox.json`.
- Run `./run-development.sh` with no arguments. Never pass JSON through the command line.
- Use only `stdout` as the protocol response. It is either `stop`, a
  `HARNESS PROTOCOL ERROR: ...`, or an instruction containing `<input>` and `<response>`.
- For an instruction, perform `<input>`, fill the exact JSON shape from `<response>`, write it
  to the inbox, and invoke the harness again. Do not add fields or prose.
- When `<input>` separately asks you to write a file to a specific path (e.g.
  `.harness/plan.json` for the feature-list array), write that file too — as its own real
  file, in raw form, never escaped as a string value inside the inbox envelope. If you
  cannot produce that file's content, do not fabricate a placeholder or send the literal
  token names back in `args`; retry the step instead.
- On a protocol error, correct the envelope and retry. Diagnostics on `stderr` do not choose
  the next state.

The durable context is in `.harness/feature_list.json`, the target's `progress.txt`, and Git.
When `<input>` starts with `=== NEW SESSION (clean context) ===`, spawn a clean-context
sub-agent for that feature and let it recover only from those artifacts. Without the marker,
continue in the current context.

## Run

Start by writing:

```json
{"type":"text","value":"start","context":{"driver":"devin"}}
```

Continue until `stdout` is exactly `stop`, then report that all features pass. Session usage
reporting is not available for Devin because there is no `scripts/devin_usage.py` driver.
