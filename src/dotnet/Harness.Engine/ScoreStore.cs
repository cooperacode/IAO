using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Persiste o resultado de cada avaliação em <c>.harness/scores.jsonl</c> (uma linha por
/// run). Mora na engine porque a serialização AOT-safe depende do <see cref="HarnessJsonContext"/>,
/// que é interno ao assembly. É o lado "notas" da Telemetria (#7), consumido por relatórios.
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
            Console.Error.WriteLine($"[ScoreStore] falha ao gravar: {ex.Message}");
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
            Console.Error.WriteLine($"[ScoreStore] falha ao carregar: {ex.Message}");
            return [];
        }
    }
}

/// <summary>
/// Nota de uma avaliação: o veredito do portão determinístico (0 tokens) e, quando ele
/// passa, a nota do juiz-LLM. <see cref="JudgeScore"/> = 0 quando o portão reprova.
/// </summary>
public record ScoreReport(
    string Timestamp,
    bool GatePassed,
    string GateDetail,
    int JudgeScore,
    string JudgeRationale);
