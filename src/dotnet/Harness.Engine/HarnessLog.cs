namespace Harness.Engine;

/// <summary>
/// Append-only, human-readable engine log at <c>.harness/harness.log</c> — persisted
/// counterpart to what today only reaches ephemeral stderr (<see cref="Error"/>), plus the
/// step entry/exit markers (<see cref="Info"/>, written by <see cref="TaskRegistry"/>) that
/// make an in-flight step observable before it completes. <see cref="Trace"/> only records a
/// COMPLETED turn — during a slow step, or one that crashes mid-flight, trace.jsonl alone
/// gives no evidence the harness ever picked up the work. This file is that evidence.
///
/// Deliberately separate from trace.jsonl: the trace is a hash-chained, one-line-per-turn
/// audit artifact consumed by evaluators and cost-correlation tooling — doubling it with
/// entry/exit lines would break that "one line = one turn" contract for every consumer.
/// harness.log carries no such contract; it's free-form and append-only.
/// </summary>
public static class HarnessLog
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/harness.log";

    /// <summary>Truncates the log at the start of a new workflow (alongside <see cref="Trace.Reset"/>).</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HarnessLog] failed to clear: {ex.Message}");
        }
    }

    /// <summary>Liveness/diagnostic events (step entry/exit) — file only, no stderr echo per turn.</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>
    /// Every harness-level failure — protocol errors, guard cutoffs, store I/O failures,
    /// unhandled faults. Writes to stderr too (existing visible behavior every call site
    /// already relied on) so this is a drop-in replacement for the raw
    /// <see cref="Console.Error"/> calls scattered across the engine.
    /// </summary>
    public static void Error(string message)
    {
        Console.Error.WriteLine(message);
        Write("ERROR", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var line = $"[{DateTimeOffset.UtcNow:O}] [{level}] {message}";
            File.AppendAllText(FilePath, line + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HarnessLog] failed to write: {ex.Message}");
        }
    }
}
