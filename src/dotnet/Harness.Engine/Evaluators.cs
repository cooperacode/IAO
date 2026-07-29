using System.Text.RegularExpressions;

namespace Harness.Engine;

/// <summary>
/// Deterministic evaluators (Exact Match, Regex, Trajectory) — the part of the diagram's
/// Evaluator #6 that does NOT need an LLM. They run in-process over <see cref="Trace"/>
/// and <see cref="HarnessState"/>, cost zero tokens, and serve as a gate: only when they
/// pass is it worth escalating to the LLM judge (a saving under the token budget).
/// </summary>
public static class Evaluators
{
    public static Score ExactMatch(string expected, string actual) =>
        new("exact_match", Norm(expected) == Norm(actual) ? 1.0 : 0.0,
            $"expected=\"{expected}\" actual=\"{actual}\"");

    public static Score MatchesRegex(string pattern, string actual) =>
        new("regex", Regex.IsMatch(actual, pattern) ? 1.0 : 0.0, pattern);

    /// <summary>
    /// Fraction of the expected prefix that matched, in order. An out-of-sequence step
    /// cuts the count right there — trajectory is about path, not set.
    /// </summary>
    public static Score Trajectory(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var matched = 0;
        for (var i = 0; i < expected.Count && i < actual.Count; i++)
        {
            if (expected[i] != actual[i])
                break;
            matched++;
        }

        var value = expected.Count == 0 ? 1.0 : (double)matched / expected.Count;
        return new("trajectory", value, $"{matched}/{expected.Count} steps in the expected order");
    }

    /// <summary>Were all expected domain keys filled in the final state?</summary>
    public static Score Completeness(HarnessState state, IReadOnlyList<string> requiredKeys)
    {
        var filled = requiredKeys.Count(k => !string.IsNullOrWhiteSpace(state.Data.GetValueOrDefault(k, "")));
        var value = requiredKeys.Count == 0 ? 1.0 : (double)filled / requiredKeys.Count;
        return new("completeness", value, $"{filled}/{requiredKeys.Count} keys filled");
    }

    /// <summary>
    /// Ended in <see cref="TraceOutcome.Stop"/> without hitting the step ceiling or the
    /// time one (<see cref="TraceOutcome.Timeout"/>) — both would be indistinguishable
    /// from a simply incomplete trajectory if not checked separately.
    /// </summary>
    public static Score StepBudget(IReadOnlyList<TraceEntry> trace)
    {
        var hitBudget = trace.Any(e => e.Outcome == TraceOutcome.Budget);
        var hitTimeout = trace.Any(e => e.Outcome == TraceOutcome.Timeout);
        var terminated = trace.Any(e => e.Outcome == TraceOutcome.Stop);

        return new("budget", !hitBudget && !hitTimeout && terminated ? 1.0 : 0.0,
            hitBudget ? "cut off by the step ceiling"
            : hitTimeout ? "cut off by the time ceiling (timeout)"
            : terminated ? "completed within budget"
            : "did not finish");
    }

    /// <summary>Trace commands in order, skipping corrective-error turns by default.</summary>
    public static IReadOnlyList<string> CommandsOf(IReadOnlyList<TraceEntry> trace, bool includeErrors = false) =>
        trace.Where(e => includeErrors || e.Outcome != TraceOutcome.Error)
             .Select(e => e.Command)
             .ToList();

    private static string Norm(string value) => value.Trim();
}

/// <summary>Score of a metric in [0,1]. <see cref="Passed"/> requires a full match.</summary>
public record Score(string Metric, double Value, string Detail = "")
{
    public bool Passed => Value >= 1.0;
}
