using Harness.Engine;
using Flows.Development;

// "Long-running agent" pattern: initializer + loop of fresh sessions, one feature at a time.
// No orchestration here — dispatch, guards, and transport live in Harness.Engine.
// start → plan → [implement → verify(auto-handoff)]*
var tasks = new Dictionary<string, Func<Envelope?, string>>
{
    ["start"] = _ => DevelopmentTasks.Start(),
    ["plan"] = envelope => DevelopmentTasks.Plan(envelope),
    ["bearings"] = envelope => DevelopmentTasks.Bearings(envelope),
    ["smoke"] = envelope => DevelopmentTasks.Smoke(envelope),
    ["pick"] = envelope => DevelopmentTasks.Pick(envelope),
    ["implement"] = envelope => DevelopmentTasks.Implement(envelope),
    ["verify"] = envelope => DevelopmentTasks.Verify(envelope),
    ["handoff"] = envelope => DevelopmentTasks.Handoff(envelope),
};

// Contextual expectation per command; a rejection becomes a corrective error (the driver
// fixes and resends). Bearings, smoke, and pick are retained as compatibility commands, but
// the normal path executes them internally; their textual payloads are ignored. `plan` no
// longer carries the feature array in its args (see DevelopmentTasks.PlanFilePath) —
// Plan() itself validates the file's content and re-requests it via PlanRetryPrompt, so no
// envelope-arg validator applies here either.
var validators = new Dictionary<string, Func<Envelope, ValidationResult>>();

// Own snapshots: if this flow shares `.harness/` with refinement+evaluation (same
// workspace), it must NOT overwrite the last-run.* that evaluation consumes. Freezes at
// its own path — like evaluation does with last-evaluation.*.
// maxSteps: override of the global ceiling (12) — this flow is long-running and needs
// slack for the loop.
// shouldResetOnStart: a "start" also arrives on the per-feature hard reset (a fresh
// session reopening a run in progress) — it's only a genuinely new run when there's no
// pending feature.
return HarnessHost.Run(
    args, tasks,
    traceSnapshotPath: ".harness/last-development.trace.jsonl",
    stateSnapshotPath: ".harness/last-development.state.json",
    validators: validators,
    maxSteps: DevelopmentTasks.StepBudget,
    shouldResetOnStart: () => FeatureStore.PendingCount() == 0);
