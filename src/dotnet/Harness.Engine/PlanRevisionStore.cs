using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Harness.Engine;

/// <summary>Persists proposed and accepted global plan revisions for audit and resume.</summary>
public static class PlanRevisionStore
{
    public const string ProposalPath = ".harness/replan.json";
    private const string Dir = ".harness/plans";
    private const string CurrentPath = ".harness/plan_revision.json";

    public static PlanRevision? ReadProposal()
    {
        try
        {
            if (!File.Exists(ProposalPath)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(ProposalPath), HarnessJsonContext.Default.PlanRevision);
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[PlanRevisionStore] failed to read proposal: {ex.Message}");
            return null;
        }
    }

    public static int RevisionCount()
    {
        try
        {
            if (!File.Exists(CurrentPath)) return 0;
            return JsonSerializer.Deserialize(File.ReadAllText(CurrentPath), HarnessJsonContext.Default.AppliedPlanRevision)?.Version ?? 0;
        }
        catch { return 0; }
    }

    public static void Record(
        PlanRevision revision, IReadOnlyList<Feature> features, PlanRevisionEvaluation evaluation)
    {
        var applied = new AppliedPlanRevision(
            RevisionCount() + 1,
            DateTimeOffset.UtcNow,
            revision.Reason,
            revision.Alternatives,
            revision.ObservationIds,
            Fingerprint(features),
            evaluation,
            [.. features]);
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(applied, PrettyFeatureListJsonContext.Default.AppliedPlanRevision);
        AtomicIO.WriteAllTextAtomic(CurrentPath, json);
        AtomicIO.WriteAllTextAtomic(Path.Combine(Dir, $"plan-v{applied.Version}.json"), json);
    }

    public static bool HasPlanFingerprint(string fingerprint)
    {
        if (!Directory.Exists(Dir)) return false;
        foreach (var path in Directory.EnumerateFiles(Dir, "plan-v*.json"))
        {
            try
            {
                var applied = JsonSerializer.Deserialize(
                    File.ReadAllText(path), HarnessJsonContext.Default.AppliedPlanRevision);
                if (applied?.PlanFingerprint == fingerprint) return true;
            }
            catch { /* corrupt history never approves a proposal */ }
        }
        return false;
    }

    public static string Fingerprint(IReadOnlyList<Feature> features)
    {
        var canonical = features.OrderBy(f => f.Id).Select(f => f with { Passes = false }).ToList();
        var json = JsonSerializer.Serialize(new FeatureList(canonical), HarnessJsonContext.Default.FeatureList);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static void Reset()
    {
        try
        {
            if (File.Exists(ProposalPath)) File.Delete(ProposalPath);
            if (File.Exists(CurrentPath)) File.Delete(CurrentPath);
            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[PlanRevisionStore] failed to reset: {ex.Message}");
        }
    }
}

public sealed record AppliedPlanRevision(
    int Version,
    DateTimeOffset AppliedAt,
    string Reason,
    string[] AlternativesConsidered,
    string[] BasedOnObservationIds,
    string PlanFingerprint,
    PlanRevisionEvaluation Evaluation,
    List<Feature> Features);
