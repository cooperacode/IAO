using Flows.Specification;

namespace Harness.Engine.Tests;

/// <summary>
/// SpecificationEvaluator holds the pure, I/O-free structural gates for the Specification
/// flow's discover/product phases (source: specs/0004-blueprint-prd-dotnet.html §5). Every
/// test here exercises "all violations reported at once with stable codes", not just the
/// happy path.
/// </summary>
public class SpecificationEvaluatorTests
{
    private static IdeaFrame ValidIdea() => new(
        SpecificationEvaluator.IdeaSchema,
        "Title",
        "Problem statement",
        ["user-1"],
        ["outcome-1"],
        ["constraint-1"],
        [new OpenQuestion("Q-1", "question?", false)]);

    private static PrdDocument ValidPrd(string ideaDigest) => new(
        SpecificationEvaluator.PrdSchema,
        ideaDigest,
        "vision",
        [new Goal("OBJ-001", "outcome")],
        [new Metric("MET-001", "OBJ-001", "measure", "target")],
        ["non-goal-1"],
        ["scope-1"],
        [new Risk("RSK-001", "desc", "mitigation", "low")],
        [new Decision("DEC-001", "statement", "rationale")],
        []);

    // ---- IdeaEvaluator ----

    [Fact]
    public void EvaluateIdea_IdeiaValida_Passa()
    {
        var result = SpecificationEvaluator.EvaluateIdea(ValidIdea(), "source.md", "sha256:abc");

        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void EvaluateIdea_SchemaDesconhecido_RetornaCodigoEstavel()
    {
        var idea = ValidIdea() with { Schema = "unknown/v9" };

        var result = SpecificationEvaluator.EvaluateIdea(idea, "source.md", "sha256:abc");

        Assert.False(result.Passed);
        Assert.Single(result.Violations, v => v.Code == "IDEA_SCHEMA_UNKNOWN");
    }

    [Fact]
    public void EvaluateIdea_CamposVaziosEQuestoesDuplicadas_RetornaTodasAsViolacoesJuntas()
    {
        var idea = ValidIdea() with
        {
            Title = "",
            Problem = "   ",
            Users = [],
            DesiredOutcomes = [],
            OpenQuestions = [new OpenQuestion("Q-1", "a?", false), new OpenQuestion("Q-1", "b?", true)],
        };

        var result = SpecificationEvaluator.EvaluateIdea(idea, "source.md", "sha256:abc");

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("IDEA_TITLE_MISSING", codes);
        Assert.Contains("IDEA_PROBLEM_MISSING", codes);
        Assert.Contains("IDEA_USERS_MISSING", codes);
        Assert.Contains("IDEA_OUTCOMES_MISSING", codes);
        Assert.Contains("IDEA_OPEN_QUESTION_DUPLICATE_ID", codes);
        // All reported together in one call, not just the first violation found.
        Assert.True(result.Violations.Length >= 5);
    }

    [Fact]
    public void EvaluateIdea_PayloadEnorme_RetornaTooLarge()
    {
        var idea = ValidIdea() with { Problem = new string('x', SpecificationEvaluator.MaxIdeaUtf8Bytes + 1) };

        var result = SpecificationEvaluator.EvaluateIdea(idea, "source.md", "sha256:abc");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "IDEA_TOO_LARGE");
    }

    [Fact]
    public void EvaluateIdea_SemFonteOuDigest_RetornaViolacoesDeProveniencia()
    {
        var result = SpecificationEvaluator.EvaluateIdea(ValidIdea(), null, "");

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("IDEA_SOURCE_MISSING", codes);
        Assert.Contains("IDEA_DIGEST_MISSING", codes);
    }

    // ---- PrdEvaluator ----

    [Fact]
    public void EvaluatePrd_PrdValidoComDigestAtual_Passa()
    {
        var result = SpecificationEvaluator.EvaluatePrd(ValidPrd("sha256:idea"), "sha256:idea");

        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void EvaluatePrd_DigestDeIdeiaDesatualizado_EhRejeitado()
    {
        var result = SpecificationEvaluator.EvaluatePrd(ValidPrd("sha256:old"), "sha256:new");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "PRD_IDEA_DIGEST_STALE");
    }

    [Fact]
    public void EvaluatePrd_IdsDuplicados_RetornaViolacaoParaCadaColecao()
    {
        var prd = ValidPrd("sha256:idea") with
        {
            Goals = [new Goal("OBJ-001", "a"), new Goal("OBJ-001", "b")],
            SuccessMetrics = [new Metric("MET-001", "OBJ-001", "m1", "t1"), new Metric("MET-001", "OBJ-001", "m2", "t2")],
            Risks = [new Risk("RSK-001", "a", "m", "low"), new Risk("RSK-001", "b", "m", "low")],
            Decisions = [new Decision("DEC-001", "a", "r"), new Decision("DEC-001", "b", "r")],
        };

        var result = SpecificationEvaluator.EvaluatePrd(prd, "sha256:idea");

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("PRD_GOAL_ID_DUPLICATE", codes);
        Assert.Contains("PRD_METRIC_ID_DUPLICATE", codes);
        Assert.Contains("PRD_RISK_ID_DUPLICATE", codes);
        Assert.Contains("PRD_DECISION_ID_DUPLICATE", codes);
    }

    [Fact]
    public void EvaluatePrd_ColecoesObrigatoriasVazias_RetornaUmaViolacaoPorColecao()
    {
        var prd = ValidPrd("sha256:idea") with
        {
            Goals = [],
            NonGoals = [],
            Scope = [],
            SuccessMetrics = [],
        };

        var result = SpecificationEvaluator.EvaluatePrd(prd, "sha256:idea");

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("PRD_GOALS_EMPTY", codes);
        Assert.Contains("PRD_NON_GOALS_EMPTY", codes);
        Assert.Contains("PRD_SCOPE_EMPTY", codes);
        Assert.Contains("PRD_METRICS_EMPTY", codes);
    }

    [Fact]
    public void EvaluatePrd_MetricaComMedidaIgualAlvo_EhRejeitada()
    {
        var prd = ValidPrd("sha256:idea") with
        {
            SuccessMetrics = [new Metric("MET-001", "OBJ-001", "same", "same")],
        };

        var result = SpecificationEvaluator.EvaluatePrd(prd, "sha256:idea");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "PRD_METRIC_MEASURE_TARGET_NOT_SEPARATE");
    }

    [Fact]
    public void EvaluatePrd_MetricaComMedidaOuAlvoVazio_EhRejeitada()
    {
        var prd = ValidPrd("sha256:idea") with
        {
            SuccessMetrics = [new Metric("MET-001", "OBJ-001", "", "target")],
        };

        var result = SpecificationEvaluator.EvaluatePrd(prd, "sha256:idea");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "PRD_METRIC_MEASURE_TARGET_NOT_SEPARATE");
    }

    [Fact]
    public void EvaluatePrd_QuestaoBloqueanteAberta_EhRejeitada()
    {
        var prd = ValidPrd("sha256:idea") with
        {
            OpenQuestions = [new OpenQuestion("Q-1", "still open?", true)],
        };

        var result = SpecificationEvaluator.EvaluatePrd(prd, "sha256:idea");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "PRD_OPEN_QUESTION_BLOCKING");
    }

    [Fact]
    public void EvaluatePrd_QuestaoNaoBloqueanteAberta_NaoImpedePasse()
    {
        var prd = ValidPrd("sha256:idea") with
        {
            OpenQuestions = [new OpenQuestion("Q-1", "nice to know", false)],
        };

        var result = SpecificationEvaluator.EvaluatePrd(prd, "sha256:idea");

        Assert.True(result.Passed);
    }
}
