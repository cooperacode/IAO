using System.Text;
using System.Text.Json;

namespace Flows.Specification;

/// <summary>
/// One structural violation found by an evaluator. <see cref="Code"/> is stable across runs
/// (never interpolates free text) so a caller — or a test — can assert on "which rule failed"
/// without string-matching a human sentence (source: specs/0004-blueprint-prd-dotnet.html §5,
/// closing note: "Evaluators retornam todos os erros de uma vez, com códigos estáveis.").
/// </summary>
public sealed record EvaluationViolation(string Code, string Message);

/// <summary>Outcome of running an evaluator: pass/fail plus every violation found, never just the first.</summary>
public sealed record EvaluationResult(bool Passed, EvaluationViolation[] Violations)
{
    public static EvaluationResult Ok() => new(true, []);
    public static EvaluationResult Fail(IReadOnlyList<EvaluationViolation> violations) => new(false, [.. violations]);
}

/// <summary>
/// Pure, I/O-free predicate evaluators for the Specification flow's documental gates
/// (blueprint 0004 §5 "Evaluators puros"). Each evaluator takes an in-memory document (plus
/// the small amount of parent-digest context needed to check freshness) and returns every
/// violation it finds in one pass — never throws, never touches disk, never judges whether
/// the content is good, only whether it is structurally valid.
/// </summary>
public static class SpecificationEvaluator
{
    public const string IdeaSchema = "iao/idea/v1";
    public const string PrdSchema = "iao/prd/v1";
    public const string SrsSchema = "iao/srs/v1";
    public const string SddSchema = "iao/sdd/v1";

    /// <summary>
    /// Upper bound on an idea's canonical JSON size (§5 "tamanho UTF-8"). Mirrors the spirit
    /// of <c>HarnessConfig.DocsMaxChars</c> — a deliberately generous ceiling that exists to
    /// catch a runaway/pasted-in payload, not to constrain a normal idea.
    /// </summary>
    public const int MaxIdeaUtf8Bytes = 20_000;

    /// <summary>
    /// Validates a <c>discover</c>-phase idea proposal: known schema, minimum required
    /// fields, UTF-8 size ceiling, unique open-question IDs, and that the proposal records
    /// where it came from (<paramref name="source"/>) and a digest of that source
    /// (<paramref name="sourceDigest"/>) — §5's "fonte e digest registrados". Those two are
    /// provenance metadata about the *input* to discovery, not fields of <see cref="IdeaFrame"/>
    /// itself, so they travel alongside it rather than living on the record.
    /// </summary>
    public static EvaluationResult EvaluateIdea(IdeaFrame idea, string? source, string? sourceDigest)
    {
        var violations = new List<EvaluationViolation>();

        if (idea.Schema != IdeaSchema)
            violations.Add(new EvaluationViolation("IDEA_SCHEMA_UNKNOWN", $"expected schema '{IdeaSchema}', got '{idea.Schema}'"));

        if (string.IsNullOrWhiteSpace(idea.Title))
            violations.Add(new EvaluationViolation("IDEA_TITLE_MISSING", "title is required"));

        if (string.IsNullOrWhiteSpace(idea.Problem))
            violations.Add(new EvaluationViolation("IDEA_PROBLEM_MISSING", "problem is required"));

        if (idea.Users.Length == 0)
            violations.Add(new EvaluationViolation("IDEA_USERS_MISSING", "at least one user is required"));

        if (idea.DesiredOutcomes.Length == 0)
            violations.Add(new EvaluationViolation("IDEA_OUTCOMES_MISSING", "at least one desired outcome is required"));

        AddDuplicateIdViolations(violations, idea.OpenQuestions.Select(q => q.Id), "IDEA_OPEN_QUESTION_DUPLICATE_ID", "open question");

        var byteCount = Utf8ByteCount(idea, SpecificationJsonContext.Default.IdeaFrame);
        if (byteCount > MaxIdeaUtf8Bytes)
            violations.Add(new EvaluationViolation("IDEA_TOO_LARGE", $"idea is {byteCount} UTF-8 bytes, exceeds the {MaxIdeaUtf8Bytes}-byte limit"));

        if (string.IsNullOrWhiteSpace(source))
            violations.Add(new EvaluationViolation("IDEA_SOURCE_MISSING", "source is required"));

        if (string.IsNullOrWhiteSpace(sourceDigest))
            violations.Add(new EvaluationViolation("IDEA_DIGEST_MISSING", "source digest is required"));

        return violations.Count == 0 ? EvaluationResult.Ok() : EvaluationResult.Fail(violations);
    }

    /// <summary>
    /// Validates a <c>product</c>-phase PRD proposal against the currently accepted idea's
    /// digest (<paramref name="currentIdeaDigest"/>, from <see cref="SpecificationStore"/>):
    /// digest freshness, unique goal/metric/risk/decision IDs, non-empty goal/non-goal/scope/
    /// metric sets, measure/target kept separate per metric, and zero unresolved blocking
    /// open question (§5 PrdEvaluator).
    /// </summary>
    public static EvaluationResult EvaluatePrd(PrdDocument prd, string currentIdeaDigest)
    {
        var violations = new List<EvaluationViolation>();

        if (prd.Schema != PrdSchema)
            violations.Add(new EvaluationViolation("PRD_SCHEMA_UNKNOWN", $"expected schema '{PrdSchema}', got '{prd.Schema}'"));

        if (string.IsNullOrEmpty(currentIdeaDigest) || prd.IdeaDigest != currentIdeaDigest)
            violations.Add(new EvaluationViolation("PRD_IDEA_DIGEST_STALE", $"prd.ideaDigest '{prd.IdeaDigest}' does not match the current accepted idea digest '{currentIdeaDigest}'"));

        AddDuplicateIdViolations(violations, prd.Goals.Select(g => g.Id), "PRD_GOAL_ID_DUPLICATE", "goal");
        AddDuplicateIdViolations(violations, prd.SuccessMetrics.Select(m => m.Id), "PRD_METRIC_ID_DUPLICATE", "metric");
        AddDuplicateIdViolations(violations, prd.Risks.Select(r => r.Id), "PRD_RISK_ID_DUPLICATE", "risk");
        AddDuplicateIdViolations(violations, prd.Decisions.Select(d => d.Id), "PRD_DECISION_ID_DUPLICATE", "decision");

        if (prd.Goals.Length == 0)
            violations.Add(new EvaluationViolation("PRD_GOALS_EMPTY", "at least one goal is required"));

        if (prd.NonGoals.Length == 0)
            violations.Add(new EvaluationViolation("PRD_NON_GOALS_EMPTY", "at least one non-goal is required"));

        if (prd.Scope.Length == 0)
            violations.Add(new EvaluationViolation("PRD_SCOPE_EMPTY", "at least one scope entry is required"));

        if (prd.SuccessMetrics.Length == 0)
            violations.Add(new EvaluationViolation("PRD_METRICS_EMPTY", "at least one success metric is required"));

        foreach (var metric in prd.SuccessMetrics)
        {
            var measure = metric.Measure?.Trim() ?? "";
            var target = metric.Target?.Trim() ?? "";
            if (measure.Length == 0 || target.Length == 0 || string.Equals(measure, target, StringComparison.OrdinalIgnoreCase))
                violations.Add(new EvaluationViolation("PRD_METRIC_MEASURE_TARGET_NOT_SEPARATE", $"metric '{metric.Id}' must have a measure and a target that are both present and distinct"));
        }

        var blocking = prd.OpenQuestions.Where(q => q.Blocking).Select(q => q.Id).ToArray();
        if (blocking.Length > 0)
            violations.Add(new EvaluationViolation("PRD_OPEN_QUESTION_BLOCKING", $"unresolved blocking open question(s): {string.Join(", ", blocking)}"));

        return violations.Count == 0 ? EvaluationResult.Ok() : EvaluationResult.Fail(violations);
    }

    /// <summary>
    /// Validates an <c>analysis</c>-phase SRS proposal against the currently accepted PRD's
    /// digest (<paramref name="currentPrdDigest"/>) and the accepted PRD's goal ids
    /// (<paramref name="acceptedGoalIds"/>, so goal coverage is checked against the real
    /// parent, not whatever goals the proposal itself claims to trace to): digest freshness,
    /// unique requirement IDs across functional + quality requirements, every accepted goal
    /// covered by at least one requirement, every requirement backed by at least one existing
    /// acceptance criterion, and no dangling reference anywhere — <c>dependsOn</c>,
    /// acceptance/interface/data-rule requirement links (§5 SrsEvaluator).
    /// </summary>
    public static EvaluationResult EvaluateSrs(SoftwareSpecification srs, string currentPrdDigest, string[] acceptedGoalIds)
    {
        var violations = new List<EvaluationViolation>();

        if (srs.Schema != SrsSchema)
            violations.Add(new EvaluationViolation("SRS_SCHEMA_UNKNOWN", $"expected schema '{SrsSchema}', got '{srs.Schema}'"));

        if (string.IsNullOrEmpty(currentPrdDigest) || srs.PrdDigest != currentPrdDigest)
            violations.Add(new EvaluationViolation("SRS_PRD_DIGEST_STALE", $"srs.prdDigest '{srs.PrdDigest}' does not match the current accepted PRD digest '{currentPrdDigest}'"));

        var requirements = srs.FunctionalRequirements.Concat(srs.QualityRequirements).ToArray();
        AddDuplicateIdViolations(violations, requirements.Select(r => r.Id), "SRS_REQUIREMENT_ID_DUPLICATE", "requirement");

        var requirementIds = requirements.Select(r => r.Id).ToHashSet();
        var acceptanceCriterionIds = srs.AcceptanceCriteria.Select(a => a.Id).ToHashSet();

        var coveredGoalIds = requirements.SelectMany(r => r.GoalIds).ToHashSet();
        foreach (var goalId in acceptedGoalIds)
            if (!coveredGoalIds.Contains(goalId))
                violations.Add(new EvaluationViolation("SRS_GOAL_NOT_COVERED", $"goal '{goalId}' is not covered by any requirement"));

        foreach (var requirement in requirements)
        {
            if (requirement.AcceptanceIds.Length == 0)
                violations.Add(new EvaluationViolation("SRS_REQUIREMENT_WITHOUT_ACCEPTANCE", $"requirement '{requirement.Id}' has no acceptance criterion"));

            foreach (var acceptanceId in requirement.AcceptanceIds)
                if (!acceptanceCriterionIds.Contains(acceptanceId))
                    violations.Add(new EvaluationViolation("SRS_ACCEPTANCE_REFERENCE_DANGLING", $"requirement '{requirement.Id}' references unknown acceptance criterion '{acceptanceId}'"));

            foreach (var dependencyId in requirement.DependsOn)
                if (!requirementIds.Contains(dependencyId))
                    violations.Add(new EvaluationViolation("SRS_REQUIREMENT_DEPENDENCY_DANGLING", $"requirement '{requirement.Id}' depends on unknown requirement '{dependencyId}'"));
        }

        foreach (var criterion in srs.AcceptanceCriteria)
            foreach (var requirementId in criterion.RequirementIds)
                if (!requirementIds.Contains(requirementId))
                    violations.Add(new EvaluationViolation("SRS_ACCEPTANCE_CRITERION_REQUIREMENT_DANGLING", $"acceptance criterion '{criterion.Id}' references unknown requirement '{requirementId}'"));

        foreach (var iface in srs.Interfaces)
            foreach (var requirementId in iface.RequirementIds)
                if (!requirementIds.Contains(requirementId))
                    violations.Add(new EvaluationViolation("SRS_INTERFACE_REQUIREMENT_DANGLING", $"interface '{iface.Id}' references unknown requirement '{requirementId}'"));

        foreach (var rule in srs.DataRules)
            foreach (var requirementId in rule.RequirementIds)
                if (!requirementIds.Contains(requirementId))
                    violations.Add(new EvaluationViolation("SRS_DATA_RULE_REQUIREMENT_DANGLING", $"data rule '{rule.Id}' references unknown requirement '{requirementId}'"));

        return violations.Count == 0 ? EvaluationResult.Ok() : EvaluationResult.Fail(violations);
    }

    /// <summary>
    /// Validates a <c>design</c>-phase SDD proposal against the currently accepted SRS's
    /// digest (<paramref name="currentSrsDigest"/>) and the accepted SRS's requirement ids
    /// (<paramref name="requirementIds"/>, functional + quality combined): digest freshness,
    /// unique ADR IDs, every accepted requirement allocated to at least one ADR, and no
    /// dangling requirement reference from an ADR or a control (§5 SddEvaluator).
    /// </summary>
    public static EvaluationResult EvaluateSdd(SoftwareDesignDocument sdd, string currentSrsDigest, string[] requirementIds)
    {
        var violations = new List<EvaluationViolation>();

        if (sdd.Schema != SddSchema)
            violations.Add(new EvaluationViolation("SDD_SCHEMA_UNKNOWN", $"expected schema '{SddSchema}', got '{sdd.Schema}'"));

        if (string.IsNullOrEmpty(currentSrsDigest) || sdd.SrsDigest != currentSrsDigest)
            violations.Add(new EvaluationViolation("SDD_SRS_DIGEST_STALE", $"sdd.srsDigest '{sdd.SrsDigest}' does not match the current accepted SRS digest '{currentSrsDigest}'"));

        AddDuplicateIdViolations(violations, sdd.Adrs.Select(a => a.Id), "SDD_ADR_ID_DUPLICATE", "ADR");

        var knownRequirementIds = requirementIds.ToHashSet();
        var allocatedRequirementIds = sdd.Adrs.SelectMany(a => a.RequirementIds).ToHashSet();
        foreach (var requirementId in requirementIds)
            if (!allocatedRequirementIds.Contains(requirementId))
                violations.Add(new EvaluationViolation("SDD_REQUIREMENT_NOT_ALLOCATED", $"requirement '{requirementId}' is not allocated to any component/ADR"));

        foreach (var adr in sdd.Adrs)
            foreach (var requirementId in adr.RequirementIds)
                if (!knownRequirementIds.Contains(requirementId))
                    violations.Add(new EvaluationViolation("SDD_ADR_REQUIREMENT_DANGLING", $"ADR '{adr.Id}' references unknown requirement '{requirementId}'"));

        foreach (var control in sdd.Controls)
            foreach (var requirementId in control.RequirementIds)
                if (!knownRequirementIds.Contains(requirementId))
                    violations.Add(new EvaluationViolation("SDD_CONTROL_REQUIREMENT_DANGLING", $"control '{control.Id}' references unknown requirement '{requirementId}'"));

        return violations.Count == 0 ? EvaluationResult.Ok() : EvaluationResult.Fail(violations);
    }

    /// <summary>Verdict values a readiness proposal may declare (blueprint 0006 review state machine).</summary>
    private static readonly string[] AllowedReadinessVerdicts = ["READY", "FAIL:product", "FAIL:analysis", "FAIL:design"];

    /// <summary>
    /// Validates a <c>review</c>-phase readiness verdict proposal: <see cref="ReadinessVerdict.Verdict"/>
    /// is restricted to an explicit allowed set, and <see cref="ReadinessVerdict.Conflicts"/>/
    /// <see cref="ReadinessVerdict.Residuals"/> must be present (non-null — a source-gen'd
    /// deserialize can leave a JSON-omitted array null even though the C# type says
    /// non-nullable). A <c>FAIL:*</c> verdict is a rejection of an earlier phase, not a slice
    /// proposal, so the remaining checks apply only when <paramref name="verdict"/> is
    /// <c>"READY"</c>: at least one and at most ten slices, unique slice IDs, every
    /// requirement/ADR reference resolves against <paramref name="knownRequirementIds"/>/
    /// <paramref name="knownAdrIds"/>, every <c>dependsOn</c> id names another slice in the same
    /// proposal (never itself, never external), the <c>dependsOn</c> graph is acyclic, at least
    /// one slice has no prerequisite, and every known requirement is covered by at least one
    /// slice — the readiness slices form a complete cover (§5 ReadinessEvaluator).
    /// </summary>
    public static EvaluationResult EvaluateReadiness(ReadinessVerdict verdict, string[] knownRequirementIds, string[] knownAdrIds)
    {
        var violations = new List<EvaluationViolation>();

        if (!AllowedReadinessVerdicts.Contains(verdict.Verdict))
            violations.Add(new EvaluationViolation("READINESS_VERDICT_INVALID", $"verdict '{verdict.Verdict}' is not one of: {string.Join(", ", AllowedReadinessVerdicts)}"));

        if (verdict.Conflicts is null)
            violations.Add(new EvaluationViolation("READINESS_CONFLICTS_MISSING", "conflicts must be a non-null array (use an empty array when there are none)"));

        if (verdict.Residuals is null)
            violations.Add(new EvaluationViolation("READINESS_RESIDUALS_MISSING", "residuals must be a non-null array (use an empty array when there are none)"));

        if (verdict.Verdict != "READY")
            return violations.Count == 0 ? EvaluationResult.Ok() : EvaluationResult.Fail(violations);

        var slices = verdict.Slices ?? [];

        if (slices.Length == 0)
            violations.Add(new EvaluationViolation("READINESS_SLICES_EMPTY", "a READY verdict requires at least one readiness slice"));

        if (slices.Length > 10)
            violations.Add(new EvaluationViolation("READINESS_TOO_MANY_SLICES", $"{slices.Length} slices exceeds the 10-slice cap"));

        AddDuplicateIdViolations(violations, slices.Select(s => s.Id), "READINESS_SLICE_ID_DUPLICATE", "readiness slice");

        var sliceIds = slices.Select(s => s.Id).ToHashSet();
        var knownRequirementIdSet = knownRequirementIds.ToHashSet();
        var knownAdrIdSet = knownAdrIds.ToHashSet();

        foreach (var slice in slices)
        {
            foreach (var requirementId in slice.RequirementIds)
                if (!knownRequirementIdSet.Contains(requirementId))
                    violations.Add(new EvaluationViolation("READINESS_REQUIREMENT_REFERENCE_DANGLING", $"slice '{slice.Id}' references unknown requirement '{requirementId}'"));

            foreach (var adrId in slice.AdrIds)
                if (!knownAdrIdSet.Contains(adrId))
                    violations.Add(new EvaluationViolation("READINESS_ADR_REFERENCE_DANGLING", $"slice '{slice.Id}' references unknown ADR '{adrId}'"));

            foreach (var dependencyId in slice.DependsOn)
                if (dependencyId == slice.Id || !sliceIds.Contains(dependencyId))
                    violations.Add(new EvaluationViolation("READINESS_DEPENDENCY_REFERENCE_DANGLING", $"slice '{slice.Id}' depends on unknown or self-referencing slice '{dependencyId}'"));
        }

        if (HasReadinessDependencyCycle(slices))
            violations.Add(new EvaluationViolation("READINESS_DEPENDENCY_CYCLE", "the slice dependsOn graph contains a cycle"));

        if (slices.Length > 0 && !slices.Any(s => s.DependsOn.Length == 0))
            violations.Add(new EvaluationViolation("READINESS_NO_INITIAL_SLICE", "at least one slice must have an empty dependsOn (an initial slice with no prerequisite)"));

        var coveredRequirementIds = slices.SelectMany(s => s.RequirementIds).ToHashSet();
        foreach (var requirementId in knownRequirementIds)
            if (!coveredRequirementIds.Contains(requirementId))
                violations.Add(new EvaluationViolation("READINESS_REQUIREMENT_NOT_SLICED", $"requirement '{requirementId}' is not covered by any readiness slice"));

        return violations.Count == 0 ? EvaluationResult.Ok() : EvaluationResult.Fail(violations);
    }

    // Kahn's algorithm over the dependsOn graph. Only edges that point at another real slice in
    // the same proposal count — dangling/self references are already reported by
    // READINESS_DEPENDENCY_REFERENCE_DANGLING above and must not also poison this check. Built
    // defensively against duplicate slice IDs (already reported by
    // READINESS_SLICE_ID_DUPLICATE) so a malformed proposal can't crash the evaluator instead of
    // just failing it.
    private static bool HasReadinessDependencyCycle(ReadinessSlice[] slices)
    {
        var indegree = new Dictionary<string, int>();
        var adjacency = new Dictionary<string, List<string>>();
        foreach (var slice in slices)
        {
            indegree.TryAdd(slice.Id, 0);
            adjacency.TryAdd(slice.Id, []);
        }

        foreach (var slice in slices)
            foreach (var dependencyId in slice.DependsOn)
                if (dependencyId != slice.Id && adjacency.ContainsKey(dependencyId))
                {
                    adjacency[dependencyId].Add(slice.Id);
                    indegree[slice.Id]++;
                }

        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            visited++;
            foreach (var next in adjacency[current])
                if (--indegree[next] == 0)
                    queue.Enqueue(next);
        }

        return visited < indegree.Count;
    }

    private static void AddDuplicateIdViolations(List<EvaluationViolation> violations, IEnumerable<string> ids, string code, string kind)
    {
        var duplicates = ids
            .GroupBy(id => id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        foreach (var id in duplicates)
            violations.Add(new EvaluationViolation(code, $"duplicate {kind} id '{id}'"));
    }

    private static int Utf8ByteCount<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, typeInfo));
}
