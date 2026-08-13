using Flows.Specification;
using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// Specification flow happy path (blueprint 0004 §2):
/// start → discover → product → analysis → design → review → stop (awaiting_approval). Covers
/// resume-from-persisted-phase on a fresh "start" (kill + restart), the retry-in-place behavior
/// of discover/product/analysis/design/review on evaluator failure, and review's recascade
/// routing (blueprint 0006).
/// </summary>
public class SpecificationFlowTests : IDisposable
{
    private const string IdeaProposalPath = ".harness/specification/active/idea.proposal.json";
    private const string PrdProposalPath = ".harness/specification/active/prd.proposal.json";
    private const string SrsProposalPath = ".harness/specification/active/srs.proposal.json";
    private const string SddProposalPath = ".harness/specification/active/sdd.proposal.json";
    private const string ReviewProposalPath = ".harness/specification/active/review.proposal.json";

    private const string ValidIdeaJson =
        """
        {"schema":"iao/idea/v1","title":"Task tracker","problem":"Teams lose track of work",
        "users":["team lead"],"desiredOutcomes":["visibility into progress"],
        "constraints":[],"openQuestions":[]}
        """;

    public SpecificationFlowTests() => Clean();

    public void Dispose() => Clean();

    private static void Clean()
    {
        StateStore.Reset();
        Trace.Reset();
        SpecificationStore.Reset();
        foreach (var path in new[] { IdeaProposalPath, PrdProposalPath, SrsProposalPath, SddProposalPath, ReviewProposalPath })
            if (File.Exists(path))
                File.Delete(path);
    }

    private static void WriteIdeaProposal(string json) => WriteProposal(IdeaProposalPath, json);
    private static void WritePrdProposal(string json) => WriteProposal(PrdProposalPath, json);
    private static void WriteSrsProposal(string json) => WriteProposal(SrsProposalPath, json);
    private static void WriteSddProposal(string json) => WriteProposal(SddProposalPath, json);
    private static void WriteReviewProposal(string json) => WriteProposal(ReviewProposalPath, json);

    private static void WriteProposal(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static Envelope Cmd(string value, params string[] args) => new(EnvelopeType.Command, value, args);

    private static string ValidPrdJson(string ideaDigest) =>
        $$"""
        {"schema":"iao/prd/v1","ideaDigest":"{{ideaDigest}}","vision":"Give every team visibility",
        "goals":[{"id":"G-1","statement":"Reduce lost work"}],
        "successMetrics":[{"id":"M-1","goalId":"G-1","measure":"tasks tracked","target":"100%"}],
        "nonGoals":["time tracking"],"scope":["task board"],
        "risks":[],"decisions":[],"openQuestions":[]}
        """;

    private static string ValidSrsJson(string prdDigest) =>
        $$$"""
        {"schema":"iao/srs/v1","prdDigest":"{{{prdDigest}}}",
        "functionalRequirements":[{"id":"RF-1","goalIds":["G-1"],"statement":"Track tasks","dependsOn":[],"acceptanceIds":["AC-1"]}],
        "qualityRequirements":[],
        "acceptanceCriteria":[{"id":"AC-1","requirementIds":["RF-1"],"given":"a task exists","when":"it is listed","then":"it appears"}],
        "interfaces":[],"dataRules":[],
        "delivery":{"target":"webapi","verificationStrategy":"integration tests","isBootstrap":true}}
        """;

    private static string ValidSddJson(string srsDigest) =>
        $$"""
        {"schema":"iao/sdd/v1","srsDigest":"{{srsDigest}}",
        "adrs":[{"id":"ADR-1","title":"Use REST","decision":"Expose a REST API","rationale":"Simplicity","requirementIds":["RF-1"]}],
        "controls":[]}
        """;

    // Covers RF-1 (from ValidSrsJson) and ADR-1 (from ValidSddJson) with a single initial slice
    // (empty dependsOn) — a minimal complete, acyclic cover.
    private const string ValidReadyReviewJson =
        """
        {"verdict":"READY",
        "slices":[{"id":"SL-1","classification":"core","goal":"Track tasks","inScope":["create task"],"outOfScope":[],
        "observableOutcome":"a created task appears in the list","requirementIds":["RF-1"],"adrIds":["ADR-1"],
        "dependsOn":[],"contracts":[],"happyPath":"user adds a task","failurePath":"invalid input is rejected",
        "acceptanceCriterion":"the task appears in the response","suggestedTarget":"webapi","suggestedVerificationStrategy":"integration test"}],
        "conflicts":[],"residuals":[]}
        """;

    private static string FailReviewJson(string phase) =>
        $$"""
        {"verdict":"FAIL:{{phase}}","slices":[],"conflicts":["needs rework"],"residuals":[]}
        """;

    /// <summary>Drives discover to completion and returns the accepted idea's digest.</summary>
    private static string AdvanceToProduct()
    {
        SpecificationTasks.Start();
        WriteIdeaProposal(ValidIdeaJson);
        SpecificationTasks.Discover(Cmd("discover"));
        var (_, digest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        return digest!;
    }

    /// <summary>Drives discover+product to completion and returns the accepted PRD's digest.</summary>
    private static string AdvanceToAnalysis()
    {
        var ideaDigest = AdvanceToProduct();
        WritePrdProposal(ValidPrdJson(ideaDigest));
        SpecificationTasks.Product(Cmd("product"));
        var (_, digest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        return digest!;
    }

    /// <summary>Drives discover+product+analysis to completion and returns the accepted SRS's digest.</summary>
    private static string AdvanceToDesign()
    {
        var prdDigest = AdvanceToAnalysis();
        WriteSrsProposal(ValidSrsJson(prdDigest));
        SpecificationTasks.Analysis(Cmd("analysis"));
        var (_, digest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        return digest!;
    }

    /// <summary>Drives discover+product+analysis+design to completion; phase persists as "review".</summary>
    private static string AdvanceToReview()
    {
        var srsDigest = AdvanceToDesign();
        WriteSddProposal(ValidSddJson(srsDigest));
        SpecificationTasks.Design(Cmd("design"));
        var (_, digest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        return digest!;
    }

    // --- (a) happy path -----------------------------------------------------------------

    [Fact]
    public void Design_SddValido_AceitaEAvancaParaReviewEmVezDeParar()
    {
        var srsDigest = AdvanceToDesign();
        WriteSddProposal(ValidSddJson(srsDigest));

        var result = SpecificationTasks.Design(Cmd("design"));

        Assert.Contains("\"value\":\"review\"", result);
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
        var (srs, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        var (sdd, sddDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        Assert.NotNull(srs);
        Assert.NotNull(sdd);
        Assert.NotNull(sddDigest);
    }

    [Fact]
    public void HappyPath_StartDiscoverProductAnalysisDesignReview_PausaAguardandoAprovacaoComReadinessAceito()
    {
        AdvanceToReview();
        WriteReviewProposal(ValidReadyReviewJson);

        var result = SpecificationTasks.Review(Cmd("review"));

        Assert.Equal("stop", result);
        Assert.Equal("awaiting_approval", SpecificationStore.LoadRun().Status);
        Assert.Equal("approve", SpecificationStore.LoadRun().Phase);
        var (verdict, digest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Readiness, SpecificationJsonContext.Default.ReadinessVerdict);
        Assert.NotNull(verdict);
        Assert.NotNull(digest);
        Assert.Equal("READY", verdict!.Verdict);
        Assert.Single(verdict.Slices);
    }

    [Fact]
    public void Product_PrdValido_AceitaEAvancaParaAnalysisEmVezDeParar()
    {
        var ideaDigest = AdvanceToProduct();
        WritePrdProposal(ValidPrdJson(ideaDigest));

        var result = SpecificationTasks.Product(Cmd("product"));

        Assert.Contains("\"value\":\"analysis\"", result);
        Assert.Equal("analysis", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
        var (prd, prdDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        Assert.NotNull(prd);
        Assert.NotNull(prdDigest);
        Assert.Equal("Give every team visibility", prd!.Vision);
    }

    [Fact]
    public void Analysis_SrsValido_AceitaEAvancaParaDesign()
    {
        var prdDigest = AdvanceToAnalysis();
        WriteSrsProposal(ValidSrsJson(prdDigest));

        var result = SpecificationTasks.Analysis(Cmd("analysis"));

        Assert.Contains("\"value\":\"design\"", result);
        Assert.Equal("design", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
        var (srs, srsDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        Assert.NotNull(srs);
        Assert.NotNull(srsDigest);
        Assert.Single(srs!.FunctionalRequirements);
    }

    [Fact]
    public void Start_SemRunPersistido_EmiteDiscover()
    {
        var result = SpecificationTasks.Start();

        Assert.Contains("\"value\":\"discover\"", result);
        Assert.Equal("discover", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
    }

    [Fact]
    public void Discover_PropostaValida_AceitaEAvancaParaProduct()
    {
        SpecificationTasks.Start();
        WriteIdeaProposal(ValidIdeaJson);

        var result = SpecificationTasks.Discover(Cmd("discover"));

        Assert.Contains("\"value\":\"product\"", result);
        Assert.Equal("product", SpecificationStore.LoadRun().Phase);
        var (idea, digest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        Assert.NotNull(idea);
        Assert.NotNull(digest);
        Assert.Equal("Task tracker", idea!.Title);
    }

    // --- (b) kill + restart resumes at the persisted phase -------------------------------

    [Fact]
    public void Start_ComRunEmProduct_RetomaEmVezDeReiniciarDoDiscover()
    {
        AdvanceToProduct(); // ...→ discover accepted, phase persisted as "product"

        var result = SpecificationTasks.Start(); // simulates a kill + restart mid-run

        Assert.Contains("\"value\":\"product\"", result);
        Assert.Contains(PrdProposalPath, result);
        Assert.Equal("product", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
        // The already-accepted idea survives the resume — it must not be wiped.
        var (idea, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        Assert.NotNull(idea);
    }

    [Fact]
    public void Start_ComRunEmAnalysis_RetomaEmVezDeReiniciarDoDiscover()
    {
        AdvanceToAnalysis(); // ...→ product accepted, phase persisted as "analysis"

        var result = SpecificationTasks.Start(); // simulates a kill + restart mid-run

        Assert.Contains("\"value\":\"analysis\"", result);
        Assert.Contains(SrsProposalPath, result);
        Assert.Equal("analysis", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
        var (prd, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        Assert.NotNull(prd);
    }

    [Fact]
    public void Start_ComRunEmDesign_RetomaEmVezDeReiniciarDoDiscover()
    {
        AdvanceToDesign(); // ...→ analysis accepted, phase persisted as "design"

        var result = SpecificationTasks.Start(); // simulates a kill + restart mid-run

        Assert.Contains("\"value\":\"design\"", result);
        Assert.Contains(SddProposalPath, result);
        Assert.Equal("design", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
        var (srs, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        Assert.NotNull(srs);
    }

    [Fact]
    public void Start_ComRunEmReview_RetomaEmVezDeReiniciarDoDiscover()
    {
        AdvanceToReview(); // ...→ design accepted, phase persisted as "review"

        var result = SpecificationTasks.Start(); // simulates a kill + restart mid-run

        Assert.Contains("\"value\":\"review\"", result);
        Assert.Contains(ReviewProposalPath, result);
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
        var (sdd, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        Assert.NotNull(sdd);
    }

    [Fact]
    public void Start_SemRunEmProgresso_ReiniciaDoDiscoverEDescartaAceitosAnteriores()
    {
        AdvanceToReview();
        WriteReviewProposal(ValidReadyReviewJson);
        SpecificationTasks.Review(Cmd("review")); // run reaches "awaiting_approval" (not in_progress)

        var result = SpecificationTasks.Start(); // no run in progress → genuinely new run

        Assert.Contains("\"value\":\"discover\"", result);
        Assert.Equal("discover", SpecificationStore.LoadRun().Phase);
        var (idea, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        Assert.Null(idea); // Reset() wiped the previous run's accepted artifacts
    }

    // --- (c) invalid PRD proposal retries in place with violations -----------------------

    [Fact]
    public void Product_PrdInvalido_MantemEmProductEReportaViolacoes()
    {
        AdvanceToProduct();
        // Missing goals/non-goals/scope/metrics, and a stale ideaDigest.
        WritePrdProposal(
            """{"schema":"iao/prd/v1","ideaDigest":"sha256:not-the-real-one","vision":"x","goals":[],"successMetrics":[],"nonGoals":[],"scope":[],"risks":[],"decisions":[],"openQuestions":[]}""");

        var result = SpecificationTasks.Product(Cmd("product"));

        Assert.Contains("\"value\":\"product\"", result);
        Assert.Contains("PRD_IDEA_DIGEST_STALE", result);
        Assert.Contains("PRD_GOALS_EMPTY", result);
        Assert.Equal("product", SpecificationStore.LoadRun().Phase);
        var (prd, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        Assert.Null(prd);
    }

    [Fact]
    public void Product_SemPropostaLegivel_ReemiteComMensagemDeArquivoAusente()
    {
        AdvanceToProduct(); // no prd.proposal.json written

        var result = SpecificationTasks.Product(Cmd("product"));

        Assert.Contains("\"value\":\"product\"", result);
        Assert.Contains(PrdProposalPath, result);
        Assert.Equal("product", SpecificationStore.LoadRun().Phase);
    }

    [Fact]
    public void Discover_PropostaInvalida_MantemEmDiscoverEReportaViolacoes()
    {
        SpecificationTasks.Start();
        WriteIdeaProposal(
            """{"schema":"wrong/schema","title":"","problem":"","users":[],"desiredOutcomes":[],"constraints":[],"openQuestions":[]}""");

        var result = SpecificationTasks.Discover(Cmd("discover"));

        Assert.Contains("\"value\":\"discover\"", result);
        Assert.Contains("IDEA_SCHEMA_UNKNOWN", result);
        Assert.Contains("IDEA_TITLE_MISSING", result);
        Assert.Equal("discover", SpecificationStore.LoadRun().Phase);
        var (idea, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        Assert.Null(idea);
    }

    [Fact]
    public void Analysis_SemPropostaLegivel_ReemiteComMensagemDeArquivoAusente()
    {
        AdvanceToAnalysis(); // no srs.proposal.json written

        var result = SpecificationTasks.Analysis(Cmd("analysis"));

        Assert.Contains("\"value\":\"analysis\"", result);
        Assert.Contains(SrsProposalPath, result);
        Assert.Equal("analysis", SpecificationStore.LoadRun().Phase);
    }

    [Fact]
    public void Analysis_SrsSemCoberturaDeObjetivo_MantemEmAnalysisEReportaCodigoEstavel()
    {
        var prdDigest = AdvanceToAnalysis();
        // The accepted PRD's goal "G-1" is never referenced by any requirement's goalIds.
        WriteSrsProposal(
            $$$"""
            {"schema":"iao/srs/v1","prdDigest":"{{{prdDigest}}}",
            "functionalRequirements":[{"id":"RF-1","goalIds":[],"statement":"Track tasks","dependsOn":[],"acceptanceIds":["AC-1"]}],
            "qualityRequirements":[],
            "acceptanceCriteria":[{"id":"AC-1","requirementIds":["RF-1"],"given":"a","when":"b","then":"c"}],
            "interfaces":[],"dataRules":[],
            "delivery":{"target":"webapi","verificationStrategy":"integration tests","isBootstrap":true}}
            """);

        var result = SpecificationTasks.Analysis(Cmd("analysis"));

        Assert.Contains("\"value\":\"analysis\"", result);
        Assert.Contains("SRS_GOAL_NOT_COVERED", result);
        Assert.Equal("analysis", SpecificationStore.LoadRun().Phase);
        var (srs, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        Assert.Null(srs);
    }

    [Fact]
    public void Design_SemPropostaLegivel_ReemiteComMensagemDeArquivoAusente()
    {
        AdvanceToDesign(); // no sdd.proposal.json written

        var result = SpecificationTasks.Design(Cmd("design"));

        Assert.Contains("\"value\":\"design\"", result);
        Assert.Contains(SddProposalPath, result);
        Assert.Equal("design", SpecificationStore.LoadRun().Phase);
    }

    [Fact]
    public void Design_RequisitoSemAlocacaoParaAdr_MantemEmDesignEReportaCodigoEstavel()
    {
        var srsDigest = AdvanceToDesign();
        // The accepted SRS's requirement "RF-1" is never allocated to any ADR.
        WriteSddProposal(
            $$"""
            {"schema":"iao/sdd/v1","srsDigest":"{{srsDigest}}","adrs":[],"controls":[]}
            """);

        var result = SpecificationTasks.Design(Cmd("design"));

        Assert.Contains("\"value\":\"design\"", result);
        Assert.Contains("SDD_REQUIREMENT_NOT_ALLOCATED", result);
        Assert.Equal("design", SpecificationStore.LoadRun().Phase);
        var (sdd, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        Assert.Null(sdd);
    }

    // --- (d) review phase: structural retry-in-place, READY, and recascade routing --------

    [Fact]
    public void Review_SemPropostaLegivel_ReemiteComMensagemDeArquivoAusente()
    {
        AdvanceToReview(); // no review.proposal.json written

        var result = SpecificationTasks.Review(Cmd("review"));

        Assert.Contains("\"value\":\"review\"", result);
        Assert.Contains(ReviewProposalPath, result);
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);
    }

    [Fact]
    public void Review_VerdictInvalido_MantemEmReviewEReportaCodigoEstavel()
    {
        AdvanceToReview();
        WriteReviewProposal("""{"verdict":"MAYBE","slices":[],"conflicts":[],"residuals":[]}""");

        var result = SpecificationTasks.Review(Cmd("review"));

        Assert.Contains("\"value\":\"review\"", result);
        Assert.Contains("READINESS_VERDICT_INVALID", result);
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);
        var (verdict, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Readiness, SpecificationJsonContext.Default.ReadinessVerdict);
        Assert.Null(verdict);
    }

    [Fact]
    public void Review_FailProduct_RecascadeiaParaProductComContadorIncrementado()
    {
        AdvanceToReview();
        WriteReviewProposal(FailReviewJson("product"));

        var result = SpecificationTasks.Review(Cmd("review"));

        Assert.Contains("\"value\":\"product\"", result);
        Assert.Equal("product", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
        Assert.Equal(1, SpecificationStore.LoadRun().Counters["recascades"]);
    }

    [Fact]
    public void Review_FailAnalysis_RecascadeiaParaAnalysisERetornaAteReviewNovamenteViaDesign()
    {
        AdvanceToReview();
        WriteReviewProposal(FailReviewJson("analysis"));

        var routeResult = SpecificationTasks.Review(Cmd("review"));

        Assert.Contains("\"value\":\"analysis\"", routeResult);
        Assert.Equal("analysis", SpecificationStore.LoadRun().Phase);
        Assert.Equal(1, SpecificationStore.LoadRun().Counters["recascades"]);

        // Re-walks forward: analysis -> design -> review, exactly like the first pass.
        var (prd, prdDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        Assert.NotNull(prd);
        WriteSrsProposal(ValidSrsJson(prdDigest!));
        var analysisResult = SpecificationTasks.Analysis(Cmd("analysis"));
        Assert.Contains("\"value\":\"design\"", analysisResult);
        Assert.Equal("design", SpecificationStore.LoadRun().Phase);

        var (srs, srsDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        Assert.NotNull(srs);
        WriteSddProposal(ValidSddJson(srsDigest!));
        var designResult = SpecificationTasks.Design(Cmd("design"));
        Assert.Contains("\"value\":\"review\"", designResult);
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);

        // The recascade counter survived the forward walk through analysis/design.
        Assert.Equal(1, SpecificationStore.LoadRun().Counters["recascades"]);
    }

    [Fact]
    public void Review_FailDesign_RecascadeiaParaDesignERetornaAteReviewNovamente()
    {
        AdvanceToReview();
        WriteReviewProposal(FailReviewJson("design"));

        var routeResult = SpecificationTasks.Review(Cmd("review"));

        Assert.Contains("\"value\":\"design\"", routeResult);
        Assert.Equal("design", SpecificationStore.LoadRun().Phase);

        var (srs, srsDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        Assert.NotNull(srs);
        WriteSddProposal(ValidSddJson(srsDigest!));
        var designResult = SpecificationTasks.Design(Cmd("design"));

        Assert.Contains("\"value\":\"review\"", designResult);
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);
        Assert.Equal(1, SpecificationStore.LoadRun().Counters["recascades"]);
    }

    [Fact]
    public void Review_TerceiraTentativaDeRecascade_ParaComNeedsHumanDecisionEmVezDeNovoRetry()
    {
        AdvanceToReview();

        // 1st recascade: FAIL:design -> back to design -> resubmit -> review again.
        WriteReviewProposal(FailReviewJson("design"));
        SpecificationTasks.Review(Cmd("review"));
        Assert.Equal("design", SpecificationStore.LoadRun().Phase);
        var (_, srsDigest1) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        WriteSddProposal(ValidSddJson(srsDigest1!));
        SpecificationTasks.Design(Cmd("design"));
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);
        Assert.Equal(1, SpecificationStore.LoadRun().Counters["recascades"]);

        // 2nd recascade: FAIL:design again -> back to design -> resubmit -> review again.
        WriteReviewProposal(FailReviewJson("design"));
        SpecificationTasks.Review(Cmd("review"));
        Assert.Equal("design", SpecificationStore.LoadRun().Phase);
        var (_, srsDigest2) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        WriteSddProposal(ValidSddJson(srsDigest2!));
        SpecificationTasks.Design(Cmd("design"));
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);
        Assert.Equal(2, SpecificationStore.LoadRun().Counters["recascades"]);

        // 3rd attempt hits the two-recascade budget: terminal, not another retry.
        WriteReviewProposal(FailReviewJson("design"));
        var thirdResult = SpecificationTasks.Review(Cmd("review"));

        Assert.Equal("stop", thirdResult);
        Assert.Equal("needs_human_decision", SpecificationStore.LoadRun().Status);
        Assert.Equal("recascade limit reached", SpecificationStore.LoadRun().TerminalReason);
        // Still parked at "review" — the third FAIL was rejected before it could route anywhere.
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);
    }
}
