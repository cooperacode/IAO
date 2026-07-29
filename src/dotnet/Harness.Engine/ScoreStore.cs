using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Persists each evaluation's result to <c>.harness/scores.jsonl</c> (one line per run).
/// Lives in the engine because AOT-safe serialization depends on
/// <see cref="HarnessJsonContext"/>, which is internal to the assembly. It's the "scores"
/// side of Telemetry (#7), consumed by reports.
/// </summary>
public static class ScoreStore
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/scores.jsonl";

    public static void Append(ScoreReport report)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var line = JsonSerializer.Serialize(report, HarnessJsonContext.Default.ScoreReport);
            File.AppendAllText(FilePath, line + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ScoreStore] failed to write: {ex.Message}");
        }
    }

    public static IReadOnlyList<ScoreReport> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];

            return File.ReadAllLines(FilePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize(line, HarnessJsonContext.Default.ScoreReport))
                .OfType<ScoreReport>()
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ScoreStore] failed to load: {ex.Message}");
            return [];
        }
    }
}

/// <summary>
/// One evaluation's score: the deterministic gate's verdict (0 tokens) and, when it
/// passes, the LLM judge's score. <see cref="JudgeScore"/> = 0 when the gate fails.
/// </summary>
public record ScoreReport(
    string Timestamp,
    bool GatePassed,
    string GateDetail,
    int JudgeScore,
    string JudgeRationale);
