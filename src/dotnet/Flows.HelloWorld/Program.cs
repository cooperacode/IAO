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

var validators = new Dictionary<string, Func<Envelope, ValidationResult>>()
{
    ["ping"] = envelope =>
    {
        var arg0 = HelloWorldTasks.ArgAt(envelope, 0, "<null>");
        if (arg0 != "ping")
            return ValidationResult.Fail($"Expected 'ping' but got '{arg0}'");
        else
            return ValidationResult.Pass;
    },
    ["pong"] = envelope =>
    {
        var arg0 = HelloWorldTasks.ArgAt(envelope, 0, "<null>");
        if (arg0 != "pong")
            return ValidationResult.Fail($"Expected 'pong' but got '{arg0}'");
        else
            return ValidationResult.Pass;
    },
};

// Own snapshots: if this flow shares `.harness/` with development (same
// workspace), it must NOT overwrite the last-run.* that development uses.
return HarnessHost.Run(
    args, tasks,
    validators: validators,
    traceSnapshotPath: ".harness/last-helloworld.trace.jsonl",
    stateSnapshotPath: ".harness/last-helloworld.state.json");
