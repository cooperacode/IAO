using Harness.Engine;
using Flows.Development;

// "Long-running agent" pattern: initializer + loop of fresh sessions, one feature at a time.
// No orchestration here — dispatch, guards, and transport live in Harness.Engine.
// start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
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
// fixes and resends). `pick` has no validator — it doesn't carry a driver artifact (the
// selection is the harness's).
var validators = new Dictionary<string, Func<Envelope, ValidationResult>>
{
    ["plan"] = EnvelopeValidation.NotEmpty("the JSON array of features [{id,title,priority}]"),
    ["bearings"] = EnvelopeValidation.NotEmpty("the short bearings summary (pwd, progress, git log)"),
    ["smoke"] = EnvelopeValidation.NotEmpty("the compact smoke test result (init.sh + log path)"),
    ["implement"] = EnvelopeValidation.NotEmpty("the short summary of what was implemented"),
    ["verify"] = EnvelopeValidation.Matches(
        @"^(PASS\b|FAIL\b)",
        "the compact self-verify verdict starting with PASS or FAIL: reason"),
    ["handoff"] = EnvelopeValidation.Matches(
        @"^([0-9a-f]{6,40}\b|NO_GIT:\s+\S.*)$",
        "the commit hash, or NO_GIT: reason when there is no Git repository"),
};

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
