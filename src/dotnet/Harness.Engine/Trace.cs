using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Writes one line per loop turn to <c>.harness/trace.jsonl</c>. It's the foundation for
/// both Telemetry (diagram #7) and the trajectory Evaluator (#6): <see cref="StateStore"/>
/// keeps only the final state — it overwrites <c>Data</c> on every step —, so without this
/// recorded sequence there's no way to evaluate the path the agent took.
///
/// Cost: zero tokens and one append write per invocation.
/// </summary>
public static class Trace
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/trace.jsonl";

    /// <summary>
    /// Frozen trajectory of the last refinement that ended in <c>stop</c>. <see cref="HarnessHost"/>
    /// writes here when the producing flow completes, so another flow (evaluation) can read
    /// the evidence even after its own <c>start</c> resets the live <c>trace.jsonl</c>.
    /// </summary>
    public const string LastRunPath = ".harness/last-run.trace.jsonl";

    /// <summary>
    /// Frozen trajectory of the last <b>evaluation</b> run. Its own path so that evaluation
    /// (which also ends in <c>stop</c>) doesn't overwrite refinement's evidence at
    /// <see cref="LastRunPath"/> — otherwise, a re-evaluation would read the previous
    /// evaluation's trace and spuriously fail the trajectory.
    /// </summary>
    public const string LastEvaluationPath = ".harness/last-evaluation.trace.jsonl";

    /// <summary>Truncates the trace at the start of a new workflow (alongside <see cref="StateStore.Reset"/>).</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Trace] failed to clear: {ex.Message}");
        }
    }

    public static void Append(int step, string command, string outcome, int instructionChars, string label = "")
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var prevHash = ComputePrevHash();
            var entry = new TraceEntry(step, command, outcome, instructionChars, DateTimeOffset.UtcNow, prevHash, label);
            var line = JsonSerializer.Serialize(entry, HarnessJsonContext.Default.TraceEntry);
            // A single append call for the whole line (already with prevHash embedded) — this
            // is what guarantees the event's atomicity at the file level.
            File.AppendAllText(FilePath, line + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Trace] failed to write: {ex.Message}");
        }
    }

    /// <summary>
    /// Hash chain (RFC §6.13): each line references the SHA-256 hex-lowercase of the
    /// previous line (exactly as it was written, byte for byte), making any retroactive
    /// edit/removal of the trace detectable — the chain breaks from the altered point on.
    /// Genesis (the file's first entry, including right after a <see cref="Reset"/>) uses
    /// 64 zeros.
    /// </summary>
    private static string ComputePrevHash()
    {
        var lastLine = LastNonEmptyLine();
        if (lastLine is null)
            return new string('0', 64);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lastLine));
        return Convert.ToHexStringLower(hash);
    }

    private static string? LastNonEmptyLine()
    {
        if (!File.Exists(FilePath))
            return null;

        // trace.jsonl is append-only and bounded by a run's step ceiling — reading it all
        // is acceptable here; no need to optimize for reverse block reads.
        var lines = File.ReadAllLines(FilePath);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
                return lines[i];
        }

        return null;
    }

    /// <summary>Freezes the live trace at the destination path — the evidence of the completed run.</summary>
    public static void Snapshot(string destination)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Directory.CreateDirectory(Dir);
                AtomicIO.CopyAtomic(FilePath, destination);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Trace] failed to freeze: {ex.Message}");
        }
    }

    /// <summary>Re-reads the live trace in the order it was written.</summary>
    public static IReadOnlyList<TraceEntry> Load() => LoadFrom(FilePath);

    /// <summary>Re-reads a trace from an arbitrary path — input for the evaluators (e.g. the snapshot).</summary>
    public static IReadOnlyList<TraceEntry> LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            return File.ReadAllLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize(line, HarnessJsonContext.Default.TraceEntry))
                .OfType<TraceEntry>()
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Trace] failed to load: {ex.Message}");
            return [];
        }
    }
}

/// <summary>
/// One loop turn: step, received command, outcome, cost (UTF-8 octets of the emitted
/// instruction), write timestamp, and <see cref="PrevHash"/> — the SHA-256 hex-lowercase
/// of the trace's previous line, forming the hash chain (RFC §6.13) that makes retroactive
/// edit/removal detectable. The timestamp isn't token data — it's just when the step
/// happened, same category as <see cref="Step"/>/<see cref="Outcome"/> — but it supplies
/// the missing time key to correlate each step with the real tokens the driver spent
/// deciding it (see scripts/harness_cost_correlate.py), without the harness having to
/// self-report tokens.
///
/// <see cref="Label"/> is the optional, domain-agnostic tag (e.g. "feature:3") that solves
/// the same pain point as <see cref="StateStore"/>: <see cref="Step"/> is a global counter
/// for the whole run, it doesn't identify WHICH unit of work the step belongs to. The
/// engine only carries the value — the flow decides what it means (see DevelopmentTasks.Pick).
/// </summary>
///
/// <remarks>
/// <see cref="PrevHash"/> and <see cref="Label"/> are the last positional fields, both
/// defaulting to <c>""</c> on purpose: this preserves existing positional call sites
/// (tests that construct <c>TraceEntry</c> directly, without caring about the chain or the
/// label) and allows reading a legacy <c>trace.jsonl</c>, written before these changes,
/// without throwing during deserialization.
/// </remarks>
public record TraceEntry(
    int Step, string Command, string Outcome, int InstructionChars, DateTimeOffset Timestamp,
    string PrevHash = "", string Label = "");

/// <summary>Possible outcomes of a step, recorded in <see cref="TraceEntry.Outcome"/>.</summary>
public static class TraceOutcome
{
    public const string Instruction = "instruction"; // moved on to the next step
    public const string Stop = "stop";               // normal end of the flow
    public const string Error = "error";             // typed error returned to the driver
    public const string Budget = "budget";           // cut off by the step ceiling
    public const string Timeout = "timeout";          // cut off by the per-step time ceiling
}
