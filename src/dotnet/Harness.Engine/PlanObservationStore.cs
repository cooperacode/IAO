using System.Text.Json;

namespace Harness.Engine;

/// <summary>Append-only evidence that may justify a global plan revision.</summary>
public static class PlanObservationStore
{
    private const string Path = ".harness/plan_observations.jsonl";

    public static PlanObservation Append(string kind, int? featureId, string summary, params string[] evidence)
    {
        var observation = new PlanObservation(
            $"OBS-{Load().Count + 1:D3}", kind, featureId, summary,
            evidence.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray(), DateTimeOffset.UtcNow);
        Directory.CreateDirectory(".harness");
        var json = JsonSerializer.Serialize(observation, HarnessJsonContext.Default.PlanObservation);
        File.AppendAllText(Path, json + Environment.NewLine);
        return observation;
    }

    public static IReadOnlyList<PlanObservation> Load()
    {
        if (!File.Exists(Path)) return [];
        var result = new List<PlanObservation>();
        foreach (var line in File.ReadLines(Path).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                if (JsonSerializer.Deserialize(line, HarnessJsonContext.Default.PlanObservation) is { } item)
                    result.Add(item);
            }
            catch { /* tolerate a partial final line after interruption */ }
        }
        return result;
    }

    public static void Reset()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (Exception ex) { HarnessLog.Error($"[PlanObservationStore] failed to reset: {ex.Message}"); }
    }
}

public sealed record PlanObservation(
    string Id,
    string Kind,
    int? FeatureId,
    string Summary,
    string[] Evidence,
    DateTimeOffset ObservedAt);
