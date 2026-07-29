using System.Text.RegularExpressions;

namespace Harness.Engine;

/// <summary>Result of a contextual validation: ok, or the reason for rejection (for the corrective error).</summary>
public readonly record struct ValidationResult(bool Ok, string Reason)
{
    public static ValidationResult Pass { get; } = new(true, string.Empty);
    public static ValidationResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Deterministic, cheap predicates to validate whether the value the driver returned
/// meets the task's expectation — BEFORE persisting it and continuing the flow. Failed →
/// <see cref="TaskRegistry"/> returns a typed corrective error and the driver resends
/// (corrective loop, not silent termination).
///
/// Deep semantic validation is still the LLM judge's job during evaluation; only what's
/// checkable in code, at zero token cost, lives here.
/// </summary>
public static class EnvelopeValidation
{
    /// <summary>The first arg exists and is not empty/whitespace.</summary>
    public static Func<Envelope, ValidationResult> NotEmpty(string expectation) =>
        envelope => FirstArg(envelope) is { Length: > 0 }
            ? ValidationResult.Pass
            : ValidationResult.Fail($"The expected argument came back empty. Expected: {expectation}.");

    /// <summary>The first arg has at least <paramref name="count"/> non-empty lines (counting literal <c>\n</c>).</summary>
    public static Func<Envelope, ValidationResult> MinLines(int count, string expectation) =>
        envelope =>
        {
            var lines = Lines(FirstArg(envelope));
            return lines >= count
                ? ValidationResult.Pass
                : ValidationResult.Fail(
                    $"The argument has {lines} non-empty line(s), but the task expects at least {count}. Expected: {expectation}.");
        };

    /// <summary>The first arg contains at least one number.</summary>
    public static Func<Envelope, ValidationResult> ContainsNumber(string expectation) =>
        envelope => Regex.IsMatch(FirstArg(envelope), @"\d")
            ? ValidationResult.Pass
            : ValidationResult.Fail($"The argument does not contain any number. Expected: {expectation}.");

    /// <summary>The first arg matches the pattern (case-insensitive).</summary>
    public static Func<Envelope, ValidationResult> Matches(string pattern, string expectation) =>
        envelope => Regex.IsMatch(FirstArg(envelope), pattern, RegexOptions.IgnoreCase)
            ? ValidationResult.Pass
            : ValidationResult.Fail($"The argument does not match the expected format. Expected: {expectation}.");

    /// <summary>Composition: every predicate must pass; the first one that fails supplies the reason.</summary>
    public static Func<Envelope, ValidationResult> All(params Func<Envelope, ValidationResult>[] validators) =>
        envelope =>
        {
            foreach (var validator in validators)
            {
                var result = validator(envelope);
                if (!result.Ok)
                    return result;
            }

            return ValidationResult.Pass;
        };

    private static string FirstArg(Envelope envelope) =>
        envelope.Args is { Length: > 0 } args ? args[0].Trim() : string.Empty;

    // Artifacts travel as a single-line JSON string with literal \n (see the flows'
    // "Compact" notice) — counts both real and escaped line breaks.
    private static int Lines(string value) =>
        value.Split(["\n", "\\n"], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => !string.IsNullOrWhiteSpace(line));
}
