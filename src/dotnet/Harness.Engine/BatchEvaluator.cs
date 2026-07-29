namespace Harness.Engine;

/// <summary>
/// Batch evaluation over a golden set — <c>Task Registry (#2)</c> turned into a true
/// evaluation registry: instead of MMLU/HumanEval datasets, refinement cases with the
/// expected trajectory and keys. Purely deterministic (0 tokens): compares each run's
/// recorded evidence against the case's expectation and aggregates the pass rate.
/// </summary>
public static class BatchEvaluator
{
    public static CaseResult Evaluate(GoldenCase golden, IReadOnlyList<TraceEntry> trace, HarnessState finalState) =>
        new(golden.Id,
        [
            Evaluators.Trajectory(golden.ExpectedTrajectory, Evaluators.CommandsOf(trace)),
            Evaluators.StepBudget(trace),
            Evaluators.Completeness(finalState, golden.RequiredKeys),
        ],
        golden.ExpectPass);

    public static BatchResult EvaluateAll(
        IEnumerable<(GoldenCase Golden, IReadOnlyList<TraceEntry> Trace, HarnessState State)> runs) =>
        new(runs.Select(r => Evaluate(r.Golden, r.Trace, r.State)).ToList());
}

/// <summary>
/// A golden-set case: the expectation the recorded evidence is measured against.
/// <see cref="ExpectPass"/> = <c>false</c> marks an <b>intentional negative</b> case — a
/// run that MUST fail the metrics (e.g. a perfect trajectory but missing content), used
/// to prove the evaluators catch the failure. The default is <c>true</c>.
/// </summary>
public record GoldenCase(
    string Id, string Description, string[] ExpectedTrajectory, string[] RequiredKeys, bool ExpectPass = true);

/// <summary>
/// A case's deterministic scores. <see cref="Passed"/> requires a full match on the
/// metrics; <see cref="Ok"/> is the suite's verdict — whether the case behaved as the
/// golden set expected (an intentional negative case is <see cref="Ok"/> precisely when
/// <see cref="Passed"/> is false).
/// </summary>
public record CaseResult(string Id, IReadOnlyList<Score> Scores, bool ExpectedPass = true)
{
    public bool Passed => Scores.All(s => s.Passed);
    public bool Ok => Passed == ExpectedPass;
}

/// <summary>Batch aggregate: fraction of cases that behaved as expected (CI-ready).</summary>
public record BatchResult(IReadOnlyList<CaseResult> Cases)
{
    public int Total => Cases.Count;
    public int PassedCount => Cases.Count(c => c.Ok);
    public double PassRate => Total == 0 ? 0.0 : (double)PassedCount / Total;
}
