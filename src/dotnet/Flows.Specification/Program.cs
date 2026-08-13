using Harness.Engine;
using Flows.Specification;

// Composition root for the Specification flow (blueprint 0004 §2 "State machine"). No
// orchestration here — dispatch, guards, and transport live in Harness.Engine. This slice
// wires the happy path:
// start → discover → product → analysis → design → review → (awaiting_approval) → approve → stop
// review's recascade routing (blueprint 0006) can send the run back through
// product/analysis/design before it reaches review again — see SpecificationTasks.Review.
// approve either publishes (decision "approved") and completes the run, or routes back to
// review (decision "revise") — see SpecificationTasks.Approve and SpecificationPublisher.
var tasks = new Dictionary<string, Func<Envelope?, string>>
{
    ["start"] = _ => SpecificationTasks.Start(),
    ["discover"] = envelope => SpecificationTasks.Discover(envelope),
    ["product"] = envelope => SpecificationTasks.Product(envelope),
    ["analysis"] = envelope => SpecificationTasks.Analysis(envelope),
    ["design"] = envelope => SpecificationTasks.Design(envelope),
    ["review"] = envelope => SpecificationTasks.Review(envelope),
    ["approve"] = envelope => SpecificationTasks.Approve(envelope),
};

// discover/product/analysis/design/review retry in place on evaluator failure (see
// SpecificationTasks) — the deterministic evaluators are the gate, so no envelope-arg
// validator applies here.
var validators = new Dictionary<string, Func<Envelope, ValidationResult>>();

// Own snapshots: this flow must not overwrite the last-run.* that other flows consume.
// maxSteps: SpecificationTasks.StepBudget (18, "como no flow histórico" — blueprint 0004 §2).
// shouldResetOnStart: a "start" also arrives on a fresh session reopening a run in progress
// (kill + restart) — it's only a genuinely new run when SpecificationStore reports no run in
// progress. Same idea as Flows.Development/Program.cs's
// () => FeatureStore.PendingCount() == 0, just driven by SpecificationStore.
return HarnessHost.Run(
    args, tasks,
    traceSnapshotPath: ".harness/last-specification.trace.jsonl",
    stateSnapshotPath: ".harness/last-specification.state.json",
    validators: validators,
    maxSteps: SpecificationTasks.StepBudget,
    shouldResetOnStart: () => SpecificationStore.LoadRun().Status != "in_progress");
