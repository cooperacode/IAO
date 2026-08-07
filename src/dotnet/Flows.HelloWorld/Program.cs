using Harness.Engine;
using Flows.HelloWorld;

// Teaching flow: the smallest possible IAO loop, no real LLM reasoning needed.
// No orchestration here — dispatch, guards, and transport live in Harness.Engine.
// start → ping → pong → stop
var tasks = new Dictionary<string, Func<Envelope?, string>>
{
    ["start"] = _ => HelloWorldTasks.Start(),
    ["ping"] = envelope => HelloWorldTasks.Ping(envelope),
    ["pong"] = envelope => HelloWorldTasks.Pong(envelope),
};

// Own snapshots: if this flow shares `.harness/` with development (same
// workspace), it must NOT overwrite the last-run.* that development uses.
return HarnessHost.Run(
    args, tasks,
    traceSnapshotPath: ".harness/last-helloworld.trace.jsonl",
    stateSnapshotPath: ".harness/last-helloworld.state.json");
