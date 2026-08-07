# Flows.HelloWorld

A teaching-only flow: the smallest possible IAO round trip, with no real
reasoning required from the driver. It exists to show the protocol itself —
harness prints an instruction to `stdout`, the driver (a real LLM, or a
human pretending to be one) replies with a JSON envelope, the harness
decides the next state — without any of the complexity of the real
`development` flow (features, git, verification scripts, ...).

State machine:

```
start → ping → pong → stop
```

- `start` asks the driver to reply with exactly `"ping"`.
- `ping` asks the driver to reply with exactly `"pong"`.
- `pong` ends the run (`stdout` is exactly `stop`).

This flow is intentionally **not** wired into `package.sh` or any IDE
adapter — it's meant to be built and driven by hand.

## Build

```bash
dotnet build src/dotnet/Flows.HelloWorld/Flows.HelloWorld.csproj -c Release
```

(`run-helloworld.sh`, below, also builds automatically on first use.)

## Run it by hand, pretending to be the LLM

There are two equivalent ways to send an envelope — the same two transports
every flow in this repo supports, because they're implemented once in
`Harness.Engine.TaskRegistry`, not per flow.

### Option A — argv transport

Pass the JSON envelope as the process argument:

```bash
cd src/dotnet
./run-helloworld.sh '{ "type": "text", "value": "start" }'
```

```
Execute the instruction inside the `input` tag. Then reply with the result as JSON.
...
<input>
    Reply with exactly the word "ping" — no reasoning needed, just echo it back.
</input>
<response>
    {"type":"text","value":"ping","args":[]}
</response>
```

Copy the `value` from `<response>` and send it as the next envelope:

```bash
./run-helloworld.sh '{ "type": "text", "value": "ping" }'
```

```
<input>
    You said 'ping'. Now reply with exactly the word "pong".
</input>
<response>
    {"type":"text","value":"pong","args":[]}
</response>
```

```bash
./run-helloworld.sh '{ "type": "text", "value": "pong" }'
```

```
stop
```

Three calls, three envelopes, done.

### Option B — inbox transport (`.harness/inbox.json`)

This is the transport a real IDE adapter uses: write the envelope to a file
instead of passing it as an argument, then call the script with **no**
arguments.

```bash
mkdir -p .harness
echo '{"type":"text","value":"start"}' > .harness/inbox.json
./run-helloworld.sh
# → prints the instruction asking for "ping"

echo '{"type":"text","value":"ping"}' > .harness/inbox.json
./run-helloworld.sh
# → prints the instruction asking for "pong"

echo '{"type":"text","value":"pong"}' > .harness/inbox.json
./run-helloworld.sh
# → prints "stop"
```

After each successful call, the harness moves the consumed envelope to
`.harness/inbox.consumed.json` — an audit trail of what was actually read,
kept separate from whatever you just wrote for the next step.

## Evidence after `stop`

Once the run reaches `stop`, the harness freezes two files for inspection —
this flow uses its own paths so it never collides with `.harness/last-development.*`
if you run both flows in the same workspace:

- `.harness/last-helloworld.trace.jsonl` — one line per turn (command, outcome, cost).
- `.harness/last-helloworld.state.json` — the final step counter and state.

Also present (shared machinery, not specific to this flow):

- `.harness/state.json` / `.harness/trace.jsonl` — the live state/trace, reset on the next `start`.
- `.harness/harness.log` — enter/exit log line per task.

These are the same artifacts `development` produces at a larger scale —
good to point at during the course as "this is the audit trail the pattern
gives you for free."
