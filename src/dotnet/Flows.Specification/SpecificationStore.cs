using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Harness.Engine;

namespace Flows.Specification;

/// <summary>
/// Namespaced store for the Specification flow's own artifacts, under
/// <c>.harness/specification/active/</c> — deliberately separate from
/// <c>Harness.Engine.ArtifactStore</c> (source: specs/0004-blueprint-prd-dotnet.html §4).
/// Development resets <c>ArtifactStore</c> at the start of its own run, so a Specification
/// artifact living there would be silently wiped by an unrelated flow; a dedicated directory
/// avoids that collision and lets the approved bundle's history survive past handoff.
///
/// Every phase (<c>idea</c>, <c>prd</c>, <c>srs</c>, <c>sdd</c>, <c>review</c>,
/// <c>readiness</c>, <c>approval</c>) follows the same two-file convention:
/// <c>&lt;phase&gt;.proposal.json</c> holds the agent's not-yet-validated draft,
/// <c>&lt;phase&gt;.accepted.json</c> holds the version that passed its evaluator. The
/// blueprint's literal file tree names a couple of phases' outputs slightly differently
/// (e.g. review's gate lands in <c>readiness.accepted.json</c>, approval is a single
/// <c>approval.json</c>); those are call-site concerns for the state machine/publisher
/// features that produce them; this store only needs to be able to read and write any
/// phase key under the uniform convention.
/// </summary>
public static class SpecificationStore
{
    private const string Dir = ".harness/specification/active";
    private const string RunPath = Dir + "/run.json";

    /// <summary>Canonical phase keys used across the Specification flow.</summary>
    public static class Phases
    {
        public const string Idea = "idea";
        public const string Prd = "prd";
        public const string Srs = "srs";
        public const string Sdd = "sdd";
        public const string Review = "review";
        public const string Readiness = "readiness";
        public const string Approval = "approval";
    }

    /// <summary>
    /// Persisted run state: step, status, phase, counters, trace label and terminal reason
    /// (blueprint §4 StateStore bullet). Missing/unreadable falls back to a fresh run at
    /// <c>start</c> — same tolerance as <see cref="Harness.Engine.StateStore.Load"/>, config
    /// and state are optional input, never a reason to bring the run down.
    /// </summary>
    public static RunState LoadRun()
    {
        try
        {
            if (File.Exists(RunPath))
            {
                var json = File.ReadAllText(RunPath);
                var state = JsonSerializer.Deserialize(json, SpecificationJsonContext.Default.RunState);
                if (state is not null)
                    return state with { Counters = state.Counters ?? new() };
            }
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[SpecificationStore] failed to load run: {ex.Message}");
        }

        return new RunState(0, "in_progress", "start", new Dictionary<string, int>(), null, null);
    }

    public static void SaveRun(RunState state)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            AtomicIO.WriteAllTextAtomic(RunPath, JsonSerializer.Serialize(state, SpecificationJsonContext.Default.RunState));
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[SpecificationStore] failed to save run: {ex.Message}");
        }
    }

    /// <summary>Writes the agent's not-yet-validated draft for <paramref name="phase"/>.</summary>
    public static void WriteProposal<T>(string phase, T value, JsonTypeInfo<T> typeInfo) =>
        WriteRaw(ProposalPath(phase), Canonicalize(value, typeInfo));

    /// <summary>Reads the pending proposal for <paramref name="phase"/>, if any.</summary>
    public static T? ReadProposal<T>(string phase, JsonTypeInfo<T> typeInfo) =>
        ReadRaw(ProposalPath(phase), typeInfo);

    /// <summary>
    /// Writes the evaluator-passed version for <paramref name="phase"/> and returns its
    /// digest (<c>sha256:&lt;hex&gt;</c>) — the value later phases compare against
    /// (<c>ideaDigest</c>, <c>prdDigest</c>, <c>srsDigest</c>, <c>sddDigest</c>,
    /// <c>bundleDigest</c>) to reject a stale parent. The digest is computed over the
    /// canonical serialization (source-generated, compact, fixed property order), so the
    /// same value always yields the same digest regardless of how the JSON on disk happens
    /// to be formatted.
    /// </summary>
    public static string WriteAccepted<T>(string phase, T value, JsonTypeInfo<T> typeInfo)
    {
        var canonical = Canonicalize(value, typeInfo);
        WriteRaw(AcceptedPath(phase), canonical);
        return Digest(canonical);
    }

    /// <summary>
    /// Reads the accepted version for <paramref name="phase"/> together with its digest.
    /// The digest is recomputed from the deserialized value's canonical form, not the raw
    /// file bytes, so incidental formatting differences on disk never cause a false digest
    /// mismatch downstream.
    /// </summary>
    public static (T? Value, string? Digest) ReadAccepted<T>(string phase, JsonTypeInfo<T> typeInfo)
    {
        var path = AcceptedPath(phase);

        try
        {
            if (!File.Exists(path))
                return (default, null);

            var json = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize(json, typeInfo);
            if (value is null)
                return (default, null);

            return (value, Digest(Canonicalize(value, typeInfo)));
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[SpecificationStore] failed to read accepted {phase}: {ex.Message}");
            return (default, null);
        }
    }

    /// <summary>Digest of an arbitrary value under the same canonicalization <see cref="WriteAccepted{T}"/> uses — for verifying a digest a child phase claims without re-reading the parent from disk.</summary>
    public static string DigestOf<T>(T value, JsonTypeInfo<T> typeInfo) => Digest(Canonicalize(value, typeInfo));

    /// <summary>
    /// Digest of the whole accepted chain (idea → prd → srs → sdd → readiness), in that fixed
    /// order — what <c>ApprovalDecision.BundleDigest</c> (§5 ApprovalEvaluator: "bundleDigest
    /// atual") must match. Any accepted phase changing (re-accepted after the approval preview
    /// was rendered) changes this value, which is exactly the staleness the approval gate needs
    /// to detect. A phase that isn't accepted yet contributes an empty segment rather than
    /// aborting — an incomplete chain still yields a stable (if unmatchable) digest instead of
    /// throwing.
    /// </summary>
    public static string BundleDigest()
    {
        var (_, ideaDigest) = ReadAccepted(Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        var (_, prdDigest) = ReadAccepted(Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        var (_, srsDigest) = ReadAccepted(Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        var (_, sddDigest) = ReadAccepted(Phases.Sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        var (_, readinessDigest) = ReadAccepted(Phases.Readiness, SpecificationJsonContext.Default.ReadinessVerdict);

        var joined = string.Join("|", new[]
        {
            ideaDigest ?? "",
            prdDigest ?? "",
            srsDigest ?? "",
            sddDigest ?? "",
            readinessDigest ?? "",
        });
        return Digest(joined);
    }

    /// <summary>Clears every proposal/accepted/run file for the active run — a fresh <c>start</c>, and test isolation.</summary>
    public static void Reset()
    {
        try
        {
            if (Directory.Exists(Dir))
                Directory.Delete(Dir, recursive: true);
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[SpecificationStore] failed to reset: {ex.Message}");
        }
    }

    private static string Canonicalize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    private static string Digest(string canonicalJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    private static string ProposalPath(string phase) => $"{Dir}/{phase}.proposal.json";
    private static string AcceptedPath(string phase) => $"{Dir}/{phase}.accepted.json";

    private static void WriteRaw(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            AtomicIO.WriteAllTextAtomic(path, json);
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[SpecificationStore] failed to write {path}: {ex.Message}");
        }
    }

    private static T? ReadRaw<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            if (!File.Exists(path))
                return default;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[SpecificationStore] failed to read {path}: {ex.Message}");
            return default;
        }
    }
}
