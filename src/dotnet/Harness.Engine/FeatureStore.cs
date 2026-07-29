using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Engine;

/// <summary>
/// The development flow's feature list, persisted to <c>.harness/feature_list.json</c> —
/// the "persistent artifact" that survives context hard resets: each session (one
/// feature) reads and writes here, without depending on the conversation history. All are
/// born with <see cref="Feature.Passes"/> = false; the flow turns one at a time until
/// none remain pending.
///
/// Lives in the engine because AOT-safe serialization depends on
/// <see cref="HarnessJsonContext"/>, internal to the assembly. Same tolerance as the other
/// stores: missing or unreadable → empty list, never brings down the run.
/// </summary>
public static class FeatureStore
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/feature_list.json";

    /// <summary>Character ceiling for <see cref="Feature.Description"/> — a defensive quota
    /// against a verbose driver: the description is reinjected into the <c>implement</c>
    /// prompt on every feature, so without a ceiling it silently inflates every future
    /// session's context.</summary>
    public const int DescriptionMaxChars = 700;

    /// <summary>Overwrites the whole list — used by <c>plan</c> (session 0) and MarkPassed.</summary>
    public static void Write(IReadOnlyList<Feature> features)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(
                new FeatureList([.. features]), PrettyFeatureListJsonContext.Default.FeatureList);
            AtomicIO.WriteAllTextAtomic(FilePath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FeatureStore] failed to write: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the raw feature array the driver returns in <c>plan</c>
    /// (<c>[{"id":1,"title":"...","priority":1}, ...]</c>). Forces <c>Passes = false</c>
    /// (every feature is born pending) and reindexes missing/duplicate ids by order. Empty
    /// list if the JSON doesn't parse — the caller re-issues the request (corrective loop),
    /// doesn't bring down the run. Parsing lives in the engine because
    /// <see cref="HarnessJsonContext"/> (AOT) is internal to the assembly.
    /// </summary>
    public static IReadOnlyList<Feature> Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.FeatureArray);
            if (parsed is null || parsed.Length == 0)
                return [];

            // Reindex first: DependsOn only makes sense referencing already-final ids, not
            // the raw (possibly missing/duplicated) ones that came from the driver.
            var reindexed = parsed
                .Select((f, i) => f with
                {
                    Id = f.Id > 0 ? f.Id : i + 1,
                    Passes = false,
                    DependsOn = f.Deps,
                    Description = TruncateDescription(f.Description),
                    References = f.Refs,
                })
                .ToList();

            if (DependencyGraphError(reindexed) is { } error)
            {
                Console.Error.WriteLine($"[FeatureStore] invalid dependency graph: {error}");
                return [];
            }

            return reindexed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FeatureStore] failed to parse features: {ex.Message}");
            return [];
        }
    }

    /// <summary>Cuts at <see cref="DescriptionMaxChars"/> characters — never throws, never
    /// rejects the whole feature over it, just shortens.</summary>
    private static string TruncateDescription(string? description) =>
        description is { Length: > DescriptionMaxChars } d ? d[..DescriptionMaxChars] : description ?? "";

    /// <summary>
    /// <c>null</c> if the <c>DependsOn</c> graph is valid (every id exists, no cycle);
    /// otherwise, a description of the problem. Kahn's algorithm (topological sort): a
    /// node left outside the resolved set ⇒ cycle. Checks dangling refs first — otherwise
    /// a phantom dependency would be counted as eternally unresolved and reported as a
    /// "cycle" when it's actually an invalid id. Uses a tolerant GroupBy/lookup (not a
    /// direct <c>ToDictionary</c>) because duplicate ids aren't deduplicated today by the
    /// reindex above — fixing that isn't this change's scope, just not throwing over it.
    /// </summary>
    private static string? DependencyGraphError(IReadOnlyList<Feature> features)
    {
        var validIds = features.Select(f => f.Id).ToHashSet();

        var dangling = features
            .SelectMany(f => f.Deps.Where(dep => !validIds.Contains(dep)).Select(dep => $"{f.Id}->{dep}"))
            .ToList();
        if (dangling.Count > 0)
            return $"dependsOn references nonexistent id(s): {string.Join(", ", dangling)}";

        var indegree = features.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Deps.Length);
        var dependents = features.SelectMany(f => f.Deps.Select(dep => (dep, f.Id))).ToLookup(x => x.dep, x => x.Id);

        var queue = new Queue<int>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var resolved = new HashSet<int>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!resolved.Add(id))
                continue;

            foreach (var dependent in dependents[id])
                if (indegree.ContainsKey(dependent) && --indegree[dependent] == 0)
                    queue.Enqueue(dependent);
        }

        return resolved.Count == indegree.Count
            ? null
            : $"cyclic dependency among features: {string.Join(", ", indegree.Keys.Except(resolved))}";
    }

    public static IReadOnlyList<Feature> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];

            var json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.FeatureList);
            return list?.Items ?? [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FeatureStore] failed to load: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// The next feature to implement: the highest-priority one (lowest
    /// <see cref="Feature.Priority"/>) among the READY ones (every id in
    /// <see cref="Feature.Deps"/> already has <c>Passes == true</c>); ties broken by
    /// <see cref="Feature.Id"/>. <c>null</c> when there's no ready pending feature — this
    /// can mean genuinely done (nothing pending) or blocked dependencies (see <c>Pick</c>
    /// in <c>Flows.Development</c>). Kahn's "ready set" recomputed on every call over the
    /// loaded list — no persisted graph structure.
    /// </summary>
    public static Feature? NextPending()
    {
        var features = Load();
        var passed = features.Where(f => f.Passes).Select(f => f.Id).ToHashSet();

        return features
            .Where(f => !f.Passes && f.Deps.All(passed.Contains))
            .OrderBy(f => f.Priority)
            .ThenBy(f => f.Id)
            .FirstOrDefault();
    }

    /// <summary>Marks the feature as completed and rewrites the list. No-op if the id doesn't exist.</summary>
    public static void MarkPassed(int id)
    {
        var features = Load();
        if (features.All(f => f.Id != id))
            return;

        Write([.. features.Select(f => f.Id == id ? f with { Passes = true } : f)]);
    }

    /// <summary>How many features are still left (<c>Passes == false</c>).</summary>
    public static int PendingCount() => Load().Count(f => !f.Passes);

    /// <summary>There are features and all of them passed — the loop's termination condition.</summary>
    public static bool AllPassing()
    {
        var features = Load();
        return features.Count > 0 && features.All(f => f.Passes);
    }

    /// <summary>Erases the previous run's list — the PRODUCING flow resets it on its own <c>start</c>.</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FeatureStore] failed to clear: {ex.Message}");
        }
    }
}

/// <summary>A feature from the development backlog: priority (lower = higher), whether it
/// already passes, which others (by id) it depends on, a free-form description (up to
/// <see cref="FeatureStore.DescriptionMaxChars"/> characters, reinjected into the
/// <c>implement</c> prompt), and explicit reference codes from the brief (e.g. "RF-003";
/// empty array when the brief cites none).</summary>
///
/// <remarks>
/// <c>DependsOn</c>/<c>References</c> are NULLABLE on purpose: <c>= []</c> is not a
/// compile-time constant (it can't be a record positional parameter's default); and an
/// extra <c>init</c> property outside the constructor has its initializer IGNORED by
/// <see cref="System.Text.Json"/> when the key doesn't exist in the JSON — it writes
/// <c>null</c>, not <c>[]</c>. <see cref="Deps"/> normalizes this for consumers; a
/// <c>feature_list.json</c> written by an earlier harness version (with no
/// <c>dependsOn</c>) still loads without throwing.
/// </remarks>
public record Feature(
    int Id, string Title, int Priority, bool Passes,
    int[]? DependsOn = null, string Description = "", string[]? References = null)
{
    [JsonIgnore]
    public int[] Deps => DependsOn ?? [];

    [JsonIgnore]
    public string[] Refs => References ?? [];
}

/// <summary>
/// Top-level envelope for <c>feature_list.json</c>. Dedicated type (not a bare
/// <c>List&lt;Feature&gt;</c>) so it's servable by System.Text.Json's source generator, a
/// Native AOT requirement.
/// </summary>
public record FeatureList(List<Feature> Items);
