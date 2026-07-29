namespace Harness.Engine;

/// <summary>
/// A flow's reusable entry point. A new domain only needs to define its tasks and call
/// <see cref="Run"/> — all the orchestration (dispatch, guards, transport) lives here.
/// </summary>
public static class HarnessHost
{
    public static int Run(
        string[] args,
        IReadOnlyDictionary<string, Func<Envelope?, string>> tasks,
        string traceSnapshotPath = Trace.LastRunPath,
        string stateSnapshotPath = StateStore.LastRunStatePath,
        IReadOnlyDictionary<string, Func<Envelope, ValidationResult>>? validators = null,
        int? maxSteps = null,
        Func<bool>? shouldResetOnStart = null)
    {
        var result = TaskRegistry.Dispatch(args, tasks, validators, maxSteps, shouldResetOnStart);

        // Run completed: freezes the trajectory AND final state as evidence for later
        // evaluation, before a next flow resets the live trace and state. Each flow
        // publishes to ITS OWN path (refinement to last-run.*, evaluation to
        // last-evaluation.*), so evaluation doesn't overwrite what it itself consumes.
        if (result == "stop")
        {
            Trace.Snapshot(traceSnapshotPath);
            StateStore.Snapshot(stateSnapshotPath);
        }

        // The only point that writes to stdout — the harness's transport channel.
        Console.WriteLine(result);
        return 0;
    }
}
