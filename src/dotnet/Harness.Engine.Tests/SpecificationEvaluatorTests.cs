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

    // ---- SrsEvaluator ----

    private static SoftwareSpecification ValidSrs(string prdDigest) => new(
        SpecificationEvaluator.SrsSchema,
        prdDigest,
        [new Requirement("RF-001", ["OBJ-001"], "does the thing", [], ["AC-001"])],
        [],
        [new AcceptanceCriterion("AC-001", ["RF-001"], "given", "when", "then")],
        [],
        [],
        new DeliveryContract("target", "strategy", true));

    [Fact]
    public void EvaluateSrs_SrsValidoComDigestAtualECoberturaCompleta_Passa()
    {
        var result = SpecificationEvaluator.EvaluateSrs(ValidSrs("sha256:prd"), "sha256:prd", ["OBJ-001"]);

        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void EvaluateSrs_DigestDePrdDesatualizado_EhRejeitado()
    {
        var result = SpecificationEvaluator.EvaluateSrs(ValidSrs("sha256:old"), "sha256:new", ["OBJ-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "SRS_PRD_DIGEST_STALE");
    }

    [Fact]
    public void EvaluateSrs_ObjetivoSemRequisitoQueOCubra_EhRejeitadoComCodigoEstavel()
    {
        // OBJ-002 exists on the accepted PRD, but no requirement's goalIds references it.
        var result = SpecificationEvaluator.EvaluateSrs(ValidSrs("sha256:prd"), "sha256:prd", ["OBJ-001", "OBJ-002"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.Violations, v => v.Code == "SRS_GOAL_NOT_COVERED");
        Assert.Contains("OBJ-002", violation.Message);
    }

    [Fact]
    public void EvaluateSrs_IdsDeRequisitoDuplicados_EhRejeitado()
    {
        var srs = ValidSrs("sha256:prd") with
        {
            FunctionalRequirements = [new Requirement("RF-001", ["OBJ-001"], "a", [], ["AC-001"])],
            QualityRequirements = [new Requirement("RF-001", ["OBJ-001"], "b", [], ["AC-001"])],
        };

        var result = SpecificationEvaluator.EvaluateSrs(srs, "sha256:prd", ["OBJ-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "SRS_REQUIREMENT_ID_DUPLICATE");
    }

    [Fact]
    public void EvaluateSrs_RequisitoSemCriterioDeAceitacao_EhRejeitado()
    {
        var srs = ValidSrs("sha256:prd") with
        {
            FunctionalRequirements = [new Requirement("RF-001", ["OBJ-001"], "does the thing", [], [])],
        };

        var result = SpecificationEvaluator.EvaluateSrs(srs, "sha256:prd", ["OBJ-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "SRS_REQUIREMENT_WITHOUT_ACCEPTANCE");
    }

    [Fact]
    public void EvaluateSrs_ReferenciasOrfas_SaoRejeitadas()
    {
        var srs = ValidSrs("sha256:prd") with
        {
            FunctionalRequirements = [new Requirement("RF-001", ["OBJ-001"], "does the thing", ["RF-999"], ["AC-999"])],
            Interfaces = [new InterfaceContract("IF-001", ["RF-999"], "name", "desc")],
            DataRules = [new DataRule("DR-001", ["RF-999"], "rule")],
        };

        var result = SpecificationEvaluator.EvaluateSrs(srs, "sha256:prd", ["OBJ-001"]);

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("SRS_REQUIREMENT_DEPENDENCY_DANGLING", codes);
        Assert.Contains("SRS_ACCEPTANCE_REFERENCE_DANGLING", codes);
        Assert.Contains("SRS_INTERFACE_REQUIREMENT_DANGLING", codes);
        Assert.Contains("SRS_DATA_RULE_REQUIREMENT_DANGLING", codes);
    }

    // ---- SddEvaluator ----

    private static SoftwareDesignDocument ValidSdd(string srsDigest) => new(
        SpecificationEvaluator.SddSchema,
        srsDigest,
        [new Adr("ADR-001", "title", "decision", "rationale", ["RF-001"])],
        [new InterfaceControl("IC-001", "name", "desc", ["RF-001"])]);

    [Fact]
    public void EvaluateSdd_SddValidoComDigestAtualEAlocacaoCompleta_Passa()
    {
        var result = SpecificationEvaluator.EvaluateSdd(ValidSdd("sha256:srs"), "sha256:srs", ["RF-001"]);

        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void EvaluateSdd_DigestDeSrsDesatualizado_EhRejeitado()
    {
        var result = SpecificationEvaluator.EvaluateSdd(ValidSdd("sha256:old"), "sha256:new", ["RF-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "SDD_SRS_DIGEST_STALE");
    }

    [Fact]
    public void EvaluateSdd_IdsDeAdrDuplicados_EhRejeitado()
    {
        var sdd = ValidSdd("sha256:srs") with
        {
            Adrs = [new Adr("ADR-001", "a", "d", "r", ["RF-001"]), new Adr("ADR-001", "b", "d", "r", ["RF-001"])],
        };

        var result = SpecificationEvaluator.EvaluateSdd(sdd, "sha256:srs", ["RF-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "SDD_ADR_ID_DUPLICATE");
    }

    [Fact]
    public void EvaluateSdd_RequisitoSemAlocacaoParaAdr_EhRejeitadoComCodigoEstavel()
    {
        // RF-002 exists on the accepted SRS, but no ADR's requirementIds references it.
        var result = SpecificationEvaluator.EvaluateSdd(ValidSdd("sha256:srs"), "sha256:srs", ["RF-001", "RF-002"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.Violations, v => v.Code == "SDD_REQUIREMENT_NOT_ALLOCATED");
        Assert.Contains("RF-002", violation.Message);
    }

    [Fact]
    public void EvaluateSdd_ReferenciasOrfasEmAdrOuControle_SaoRejeitadas()
    {
        var sdd = ValidSdd("sha256:srs") with
        {
            Adrs = [new Adr("ADR-001", "title", "decision", "rationale", ["RF-999"])],
            Controls = [new InterfaceControl("IC-001", "name", "desc", ["RF-999"])],
        };

        var result = SpecificationEvaluator.EvaluateSdd(sdd, "sha256:srs", ["RF-001"]);

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("SDD_ADR_REQUIREMENT_DANGLING", codes);
        Assert.Contains("SDD_CONTROL_REQUIREMENT_DANGLING", codes);
        // RF-001 is still uncovered since the only ADR points at RF-999 instead.
        Assert.Contains("SDD_REQUIREMENT_NOT_ALLOCATED", codes);
    }

    // ---- ReadinessEvaluator ----

    private static ReadinessSlice ValidReadinessSlice(string id, string[]? dependsOn = null) => new(
        id, "core", "goal", ["in-scope"], ["out-of-scope"], "observable outcome",
        ["RF-001"], ["ADR-001"], dependsOn ?? [], ["contract-1"],
        "happy path", "failure path", "acceptance criterion", "target", "strategy");

    private static ReadinessVerdict ValidReadinessVerdict() => new(
        "READY",
        [ValidReadinessSlice("SL-001")],
        [],
        []);

    [Fact]
    public void EvaluateReadiness_VerdictReadyValidoComFatiaInicialECoberturaCompleta_Passa()
    {
        var result = SpecificationEvaluator.EvaluateReadiness(ValidReadinessVerdict(), ["RF-001"], ["ADR-001"]);

        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void EvaluateReadiness_VerdictDesconhecido_RetornaCodigoEstavel()
    {
        var verdict = ValidReadinessVerdict() with { Verdict = "MAYBE" };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "READINESS_VERDICT_INVALID");
    }

    [Fact]
    public void EvaluateReadiness_VerdictFail_IgnoraValidacaoDeFatias()
    {
        // FAIL:* is a rejection of an earlier phase, not a slice proposal — an empty slice list
        // must not be treated as a violation in that case.
        var verdict = new ReadinessVerdict("FAIL:design", [], ["needs rework"], []);

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("FAIL:product")]
    [InlineData("FAIL:analysis")]
    [InlineData("FAIL:design")]
    public void EvaluateReadiness_VerdictsFailPermitidos_NaoDisparamViolacaoDeVerdict(string verdict)
    {
        var proposal = new ReadinessVerdict(verdict, [], [], []);

        var result = SpecificationEvaluator.EvaluateReadiness(proposal, ["RF-001"], ["ADR-001"]);

        Assert.DoesNotContain(result.Violations, v => v.Code == "READINESS_VERDICT_INVALID");
    }

    [Fact]
    public void EvaluateReadiness_ConflictsOuResidualsNulos_RetornaViolacoesDeProveniencia()
    {
        var verdict = ValidReadinessVerdict() with { Conflicts = null!, Residuals = null! };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("READINESS_CONFLICTS_MISSING", codes);
        Assert.Contains("READINESS_RESIDUALS_MISSING", codes);
    }

    [Fact]
    public void EvaluateReadiness_VerdictReadySemFatias_EhRejeitado()
    {
        var verdict = ValidReadinessVerdict() with { Slices = [] };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "READINESS_SLICES_EMPTY");
    }

    [Fact]
    public void EvaluateReadiness_MaisDeDezFatias_EhRejeitado()
    {
        var slices = Enumerable.Range(1, 11).Select(i => ValidReadinessSlice($"SL-{i:000}")).ToArray();
        var verdict = ValidReadinessVerdict() with { Slices = slices };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "READINESS_TOO_MANY_SLICES");
    }

    [Fact]
    public void EvaluateReadiness_IdsDeFatiaDuplicados_EhRejeitado()
    {
        var verdict = ValidReadinessVerdict() with
        {
            Slices = [ValidReadinessSlice("SL-001"), ValidReadinessSlice("SL-001")],
        };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "READINESS_SLICE_ID_DUPLICATE");
    }

    [Fact]
    public void EvaluateReadiness_ReferenciaDeRequisitoOrfa_EhRejeitadaComCodigoEstavel()
    {
        var verdict = ValidReadinessVerdict() with
        {
            Slices = [ValidReadinessSlice("SL-001") with { RequirementIds = ["RF-999"] }],
        };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.Violations, v => v.Code == "READINESS_REQUIREMENT_REFERENCE_DANGLING");
        Assert.Contains("RF-999", violation.Message);
    }

    [Fact]
    public void EvaluateReadiness_ReferenciaDeAdrOrfa_EhRejeitadaComCodigoEstavel()
    {
        var verdict = ValidReadinessVerdict() with
        {
            Slices = [ValidReadinessSlice("SL-001") with { AdrIds = ["ADR-999"] }],
        };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.Violations, v => v.Code == "READINESS_ADR_REFERENCE_DANGLING");
        Assert.Contains("ADR-999", violation.Message);
    }

    [Fact]
    public void EvaluateReadiness_DependeDeFatiaInexistenteOuDeSiMesma_EhRejeitado()
    {
        var verdict = ValidReadinessVerdict() with
        {
            Slices =
            [
                ValidReadinessSlice("SL-001", dependsOn: ["SL-999"]),
                ValidReadinessSlice("SL-002", dependsOn: ["SL-002"]),
            ],
        };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        var danglingCount = result.Violations.Count(v => v.Code == "READINESS_DEPENDENCY_REFERENCE_DANGLING");
        Assert.Equal(2, danglingCount);
    }

    [Fact]
    public void EvaluateReadiness_DependenciaCiclicaEntreDuasFatias_EhRejeitadaComCodigoEstavel()
    {
        // SL-001 -> SL-002 -> SL-001 (2-cycle): neither slice can ever be the first to run.
        var verdict = ValidReadinessVerdict() with
        {
            Slices =
            [
                ValidReadinessSlice("SL-001", dependsOn: ["SL-002"]),
                ValidReadinessSlice("SL-002", dependsOn: ["SL-001"]),
            ],
        };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "READINESS_DEPENDENCY_CYCLE");
    }

    [Fact]
    public void EvaluateReadiness_DependenciaCiclicaEntreTresFatias_EhRejeitadaComCodigoEstavel()
    {
        // SL-001 -> SL-002 -> SL-003 -> SL-001 (3-cycle).
        var verdict = ValidReadinessVerdict() with
        {
            Slices =
            [
                ValidReadinessSlice("SL-001", dependsOn: ["SL-002"]),
                ValidReadinessSlice("SL-002", dependsOn: ["SL-003"]),
                ValidReadinessSlice("SL-003", dependsOn: ["SL-001"]),
            ],
        };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "READINESS_DEPENDENCY_CYCLE");
    }

    [Fact]
    public void EvaluateReadiness_AutoDependencia_NaoDisparaCicloApenasReferenciaInvalida()
    {
        // A self-loop isn't "another slice", so it's a dangling reference, not a cycle — the
        // cycle graph must never build an edge from a slice to itself.
        var verdict = ValidReadinessVerdict() with
        {
            Slices = [ValidReadinessSlice("SL-001", dependsOn: ["SL-001"])],
        };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("READINESS_DEPENDENCY_REFERENCE_DANGLING", codes);
        Assert.DoesNotContain("READINESS_DEPENDENCY_CYCLE", codes);
    }

    [Fact]
    public void EvaluateReadiness_NenhumaFatiaInicial_EhRejeitadoComCodigoEstavel()
    {
        // Every slice declares *some* dependsOn id (one dangling), so none has an empty
        // dependsOn — no starting slice, even though the (dangling-edge-free) graph has no cycle.
        var verdict = ValidReadinessVerdict() with
        {
            Slices =
            [
                ValidReadinessSlice("SL-001", dependsOn: ["SL-002"]),
                ValidReadinessSlice("SL-002", dependsOn: ["SL-999"]),
            ],
        };

        var result = SpecificationEvaluator.EvaluateReadiness(verdict, ["RF-001"], ["ADR-001"]);

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("READINESS_NO_INITIAL_SLICE", codes);
        Assert.DoesNotContain("READINESS_DEPENDENCY_CYCLE", codes);
    }

    [Fact]
    public void EvaluateReadiness_RequisitoNaoCobertoPorNenhumaFatia_EhRejeitadoComCodigoEstavel()
    {
        // RF-002 exists on the accepted SRS, but no slice's requirementIds references it.
        var result = SpecificationEvaluator.EvaluateReadiness(ValidReadinessVerdict(), ["RF-001", "RF-002"], ["ADR-001"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.Violations, v => v.Code == "READINESS_REQUIREMENT_NOT_SLICED");
        Assert.Contains("RF-002", violation.Message);
    }

    // ---- ApprovalEvaluator ----

    private static ApprovalDecision ValidApproval(string bundleDigest) => new(
        "approved", bundleDigest, "looks solid, ready to publish", "reviewer@example.com", DateTimeOffset.UtcNow);

    [Fact]
    public void EvaluateApproval_DecisaoValidaComDigestAtual_Passa()
    {
        var result = SpecificationEvaluator.EvaluateApproval(ValidApproval("sha256:bundle"), "sha256:bundle");

        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void EvaluateApproval_DecisaoDesconhecida_EhRejeitadaComCodigoEstavel()
    {
        var decision = ValidApproval("sha256:bundle") with { Decision = "maybe" };

        var result = SpecificationEvaluator.EvaluateApproval(decision, "sha256:bundle");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "APPROVAL_DECISION_INVALID");
    }

    [Fact]
    public void EvaluateApproval_BundleDigestDesatualizado_EhRejeitadoComCodigoEstavel()
    {
        // The decision carries the OLD digest it was previewed against, but the accepted chain
        // moved on (some accepted document was re-accepted) — the current digest is different.
        var result = SpecificationEvaluator.EvaluateApproval(ValidApproval("sha256:old-preview"), "sha256:new-current");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "APPROVAL_BUNDLE_DIGEST_STALE");
    }

    [Fact]
    public void EvaluateApproval_DigestAtualVazio_EhRejeitadoComoDesatualizado()
    {
        // An empty current digest means the bundle isn't actually complete yet — no decision
        // can be bound to a bundle that doesn't exist.
        var result = SpecificationEvaluator.EvaluateApproval(ValidApproval("sha256:bundle"), "");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "APPROVAL_BUNDLE_DIGEST_STALE");
    }

    [Fact]
    public void EvaluateApproval_RationaleAusente_EhRejeitadoComCodigoEstavel()
    {
        var decision = ValidApproval("sha256:bundle") with { Rationale = "   " };

        var result = SpecificationEvaluator.EvaluateApproval(decision, "sha256:bundle");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "APPROVAL_RATIONALE_MISSING");
    }

    [Fact]
    public void EvaluateApproval_DecisaoRevise_TambemExigeDigestERationale()
    {
        var decision = new ApprovalDecision("revise", "sha256:bundle", "needs another pass on the SRS", "reviewer@example.com", DateTimeOffset.UtcNow);

        var result = SpecificationEvaluator.EvaluateApproval(decision, "sha256:bundle");

        Assert.True(result.Passed);
    }

    [Fact]
    public void EvaluateApproval_TodasAsViolacoesJuntas_SaoRetornadasDeUmaVez()
    {
        var decision = new ApprovalDecision("maybe", "sha256:stale", "", "reviewer@example.com", DateTimeOffset.UtcNow);

        var result = SpecificationEvaluator.EvaluateApproval(decision, "sha256:current");

        Assert.False(result.Passed);
        var codes = result.Violations.Select(v => v.Code).ToArray();
        Assert.Contains("APPROVAL_DECISION_INVALID", codes);
        Assert.Contains("APPROVAL_BUNDLE_DIGEST_STALE", codes);
        Assert.Contains("APPROVAL_RATIONALE_MISSING", codes);
    }
}
