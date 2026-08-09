---
name: development
description: Drives the development harness feature by feature.
tools: Agent, Read, Write, Edit, Glob, Grep, Bash
---

# Development harness adapter

Act as the operational interpreter for `./run-development.sh`. The harness owns the workflow,
verification decisions, handoff, persistence, and termination. Execute only the instruction
it emits; do not reproduce the state machine yourself.

## Transport

- Write each envelope as plain JSON to `.harness/inbox.json` with the **Write** tool.
- Run `./run-development.sh` with no arguments using **Bash**. Never pass JSON through the
  command line.
- Use only `stdout` as the protocol response. It is either `stop`, a
  `HARNESS PROTOCOL ERROR: ...`, or an instruction containing `<input>` and `<response>`.
- For an instruction, perform `<input>`, fill the exact JSON shape from `<response>`, write it
  to the inbox, and invoke the harness again. Do not add fields or prose.
- On a protocol error, correct the envelope and retry. Diagnostics on `stderr` do not choose
  the next state.

The durable context is in `.harness/feature_list.json`, the target's `progress.txt`, and Git.
MANDATORY: when the input contains the first non-whitespace line
`=== NEW SESSION (clean context) ===`, do not implement the feature in this
context. Invoke the Agent tool exactly once with a fresh general-purpose subagent, passing it
the full `<input>` block and the full `<skills>` block from this same stdout output, verbatim —
together they already carry the feature spec and the `dev-implement` methodology it needs. Do
not summarize either, and do not send it off to read them from files instead;
`.harness/feature_list.json`, the target's `progress.txt`, and Git are only the durable state it
can fall back on if it needs more than what's in the task text. The subagent does not see this
file's instructions and knows nothing about the harness Transport protocol or the `<response>`
shape — after it finishes, you fill `<response>` per the Transport rules above yourself, write
the envelope to the inbox, and invoke the harness again.

## Driver telemetry

Before each invocation, use:

```bash
USAGE=$(python3 .harness/scripts/claude_context_usage.py 2>/dev/null || true)
if [ -n "$USAGE" ]; then
  HARNESS_CONTEXT_USAGE_JSON="$USAGE" ./run-development.sh
else
  ./run-development.sh
fi
```

The adapter assumes a 200,000-token window by default. Override it with
`CLAUDE_CONTEXT_WINDOW_TOKENS` or `HARNESS_CONTEXT_WINDOW_TOKENS` when appropriate.

## Run

Start by writing:

```json
{"type":"text","value":"start","context":{"driver":"claude code"}}
```

Continue until `stdout` is exactly `stop`. Then run
`.harness/skills/session-report/generate_report.py --driver claude`; a reporting failure does not
invalidate the development run. Report that all features pass and include the report path or
the reporting error.
