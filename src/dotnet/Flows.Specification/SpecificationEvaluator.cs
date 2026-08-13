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
