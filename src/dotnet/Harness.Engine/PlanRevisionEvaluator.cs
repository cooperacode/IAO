namespace Harness.Engine;

public enum PlanRevisionVerdict { Approve, ApproveWithWarnings, Reject }

public sealed record PlanRevisionIssue(string Code, string Message);

public sealed record PlanDiff(
    int[] Added,
    int[] Removed,
    int[] Modified,
    int[] Reprioritized,
    string[] RemovedReferences,
    string[] RemovedAcceptanceCriteria)
{
    public bool HasChanges => Added.Length + Removed.Length + Modified.Length + Reprioritized.Length > 0;
}

public sealed record PlanRevisionEvaluation(
    PlanRevisionVerdict Verdict,
    PlanRevisionIssue[] Errors,
    PlanRevisionIssue[] Warnings,
    PlanDiff Diff)
{
    public bool Passed => Verdict != PlanRevisionVerdict.Reject;
}

/// <summary>Pure, deterministic gate for global plan revisions. It judges evidence and
/// invariants only; it does not interpret whether a technical strategy is semantically good.</summary>
public static class PlanRevisionEvaluator
{
    public static PlanRevisionEvaluation Evaluate(
        IReadOnlyList<Feature> current,
        PlanRevision revision,
        IReadOnlyList<PlanObservation> observations,
        int maxFeatures,
        int remainingSteps,
        int stepsPerFeature)
    {
        var errors = new List<PlanRevisionIssue>();
        var warnings = new List<PlanRevisionIssue>();
        var proposed = revision.Features;
        var currentById = current.ToDictionary(f => f.Id);
        var proposedById = proposed.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var diff = BuildDiff(current, proposed);

        if (string.IsNullOrWhiteSpace(revision.Reason))
            Error("REVISION_REASON_REQUIRED", "A revision reason is required.");

        var alternatives = revision.Alternatives
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(Normalize).Distinct().ToArray();
        if (alternatives.Length < 2)
            Error("ALTERNATIVES_REQUIRED", "At least two distinct alternatives are required.");

        if (proposed.Length == 0)
            Error("PLAN_EMPTY", "The revised plan must contain features.");
        if (proposed.Length > maxFeatures)
            Error("FEATURE_LIMIT", $"The revised plan exceeds the {maxFeatures}-feature limit.");
        if (proposed.Any(f => f.Id <= 0 || string.IsNullOrWhiteSpace(f.Title) || f.Priority <= 0))
            Error("FEATURE_INVALID", "Every feature needs a positive unique id, title and positive priority.");
        if (proposed.Select(f => f.Id).Distinct().Count() != proposed.Length)
            Error("FEATURE_ID_DUPLICATE", "Feature ids must be unique.");

        foreach (var passed in current.Where(f => f.Passes))
        {
            if (!proposedById.TryGetValue(passed.Id, out var retained))
                Error("PASSED_FEATURE_REMOVED", $"Passed feature #{passed.Id} cannot be removed.");
            else if (!SameDefinition(passed, retained))
                Error("PASSED_FEATURE_MODIFIED", $"Passed feature #{passed.Id} cannot be modified.");
        }

        ValidateGraph(proposed, current.Where(f => f.Passes).Select(f => f.Id).ToHashSet(), Error);

        foreach (var reference in diff.RemovedReferences)
            Error("REQUIREMENT_COVERAGE_REMOVED", $"Brief reference '{reference}' is no longer covered.");
        foreach (var acceptance in diff.RemovedAcceptanceCriteria)
            Error("ACCEPTANCE_REMOVED", $"Acceptance criterion '{acceptance}' is no longer covered.");

        var knownObservationIds = observations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (revision.ObservationIds.Length == 0)
            Error("OBSERVATION_REQUIRED", "The revision must cite at least one persisted observation.");
        foreach (var id in revision.ObservationIds.Distinct())
            if (!knownObservationIds.Contains(id))
                Error("OBSERVATION_UNKNOWN", $"Observation '{id}' does not exist in the run evidence.");

        if (!diff.HasChanges)
            Error("PLAN_UNCHANGED", "The revision does not change the current plan.");
        var fingerprint = PlanRevisionStore.Fingerprint(proposed);
        if (PlanRevisionStore.HasPlanFingerprint(fingerprint))
            Error("PLAN_REPEATED", "The same revised plan was already applied in this run.");

        var pending = proposed.Count(f => !currentById.TryGetValue(f.Id, out var old) || !old.Passes);
        var worstCaseSteps = pending * stepsPerFeature;
        if (remainingSteps >= 0 && worstCaseSteps > remainingSteps)
            warnings.Add(new("BUDGET_RISK", $"The revised plan may require {worstCaseSteps} steps with {remainingSteps} remaining."));

        var verdict = errors.Count > 0 ? PlanRevisionVerdict.Reject
            : warnings.Count > 0 ? PlanRevisionVerdict.ApproveWithWarnings
            : PlanRevisionVerdict.Approve;
        return new(verdict, [.. errors], [.. warnings], diff);

        void Error(string code, string message) => errors.Add(new(code, message));
    }

    private static PlanDiff BuildDiff(IReadOnlyList<Feature> current, IReadOnlyList<Feature> proposed)
    {
        var before = current.ToDictionary(f => f.Id);
        var after = proposed.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var added = after.Keys.Except(before.Keys).Order().ToArray();
        var removed = before.Keys.Except(after.Keys).Order().ToArray();
        var common = before.Keys.Intersect(after.Keys).ToArray();
        var reprioritized = common.Where(id => before[id].Priority != after[id].Priority).Order().ToArray();
        var modified = common.Where(id => !SameDefinitionExceptPriority(before[id], after[id])).Order().ToArray();

        var oldRefs = current.SelectMany(f => f.Refs).Where(NotBlank).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newRefs = proposed.SelectMany(f => f.Refs).Where(NotBlank).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldAcceptance = current.SelectMany(f => f.Context.AcceptanceItems).Where(NotBlank).Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newAcceptance = proposed.SelectMany(f => f.Context.AcceptanceItems).Where(NotBlank).Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(added, removed, modified, reprioritized,
            oldRefs.Except(newRefs).Order().ToArray(), oldAcceptance.Except(newAcceptance).Order().ToArray());
    }

    private static void ValidateGraph(
        IReadOnlyList<Feature> features, HashSet<int> passedIds, Action<string, string> error)
    {
        if (features.Select(f => f.Id).Distinct().Count() != features.Count) return;
        var ids = features.Select(f => f.Id).ToHashSet();
        foreach (var feature in features)
        {
            if (feature.Deps.Contains(feature.Id))
                error("SELF_DEPENDENCY", $"Feature #{feature.Id} depends on itself.");
            foreach (var missing in feature.Deps.Where(id => !ids.Contains(id)))
                error("DEPENDENCY_MISSING", $"Feature #{feature.Id} depends on missing feature #{missing}.");
        }
        if (features.SelectMany(f => f.Deps).Any(id => !ids.Contains(id))) return;

        var indegree = features.ToDictionary(f => f.Id, f => f.Deps.Distinct().Count());
        var dependents = features.SelectMany(f => f.Deps.Distinct().Select(dep => (dep, f.Id))).ToLookup(x => x.dep, x => x.Id);
        var queue = new Queue<int>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var resolved = 0;
        while (queue.TryDequeue(out var id))
        {
            resolved++;
            foreach (var dependent in dependents[id])
                if (--indegree[dependent] == 0) queue.Enqueue(dependent);
        }
        if (resolved != features.Count)
            error("DEPENDENCY_CYCLE", "The revised dependency graph contains a cycle.");

        var pending = features.Where(f => !passedIds.Contains(f.Id)).ToArray();
        if (pending.Length > 0 && !pending.Any(f => f.Deps.All(passedIds.Contains)))
            error("PLAN_NO_READY_FEATURE", "The revised plan has pending work but no executable feature.");
    }

    private static bool SameDefinition(Feature left, Feature right) =>
        left.Priority == right.Priority && SameDefinitionExceptPriority(left, right);

    private static bool SameDefinitionExceptPriority(Feature left, Feature right) =>
        left.Id == right.Id && left.Title == right.Title && left.Description == right.Description
        && left.Deps.SequenceEqual(right.Deps) && left.Refs.SequenceEqual(right.Refs)
        && left.Context.RequirementItems.SequenceEqual(right.Context.RequirementItems)
        && left.Context.DecisionItems.SequenceEqual(right.Context.DecisionItems)
        && left.Context.ConstraintItems.SequenceEqual(right.Context.ConstraintItems)
        && left.Context.FileItems.SequenceEqual(right.Context.FileItems)
        && left.Context.AcceptanceItems.SequenceEqual(right.Context.AcceptanceItems);

    private static bool NotBlank(string value) => !string.IsNullOrWhiteSpace(value);
    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
