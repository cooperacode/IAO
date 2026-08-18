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
    private const string ApprovalProposalPath = ".harness/specification/active/approval.proposal.json";

    // "specs" folder relative to the test process's CWD — only the approve/publish tests
    // populate it (via SpecificationPublisher), created/deleted by them.
    private static readonly string SpecsDir = Path.Combine(Directory.GetCurrentDirectory(), "specs");

    // Mirrors SpecificationTasks.SourcesFolder (private) — only the sources-ingestion tests
    // populate it, created/deleted by them.
    private static readonly string SourcesDir = Path.Combine(Directory.GetCurrentDirectory(), "specs", "sources");

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
        SpecificationStore.Reset(); // also removes publish-manifest.json (same directory)
        foreach (var path in new[] { IdeaProposalPath, PrdProposalPath, SrsProposalPath, SddProposalPath, ReviewProposalPath, ApprovalProposalPath })
            if (File.Exists(path))
                File.Delete(path);
        // SourcesDir ("specs/sources") is a child of SpecsDir ("specs") — one recursive
        // delete clears both, sources included.
        if (Directory.Exists(SpecsDir))
            Directory.Delete(SpecsDir, recursive: true);
    }

    private static void WriteIdeaProposal(string json) => WriteProposal(IdeaProposalPath, json);
    private static void WritePrdProposal(string json) => WriteProposal(PrdProposalPath, json);
    private static void WriteSrsProposal(string json) => WriteProposal(SrsProposalPath, json);
    private static void WriteSddProposal(string json) => WriteProposal(SddProposalPath, json);
    private static void WriteApprovalProposal(string json) => WriteProposal(ApprovalProposalPath, json);
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

    /// <summary>
    /// Drives the run all the way to <c>awaiting_approval</c>/<c>approve</c> (a READY review
    /// verdict) and returns the bundle digest an approval decision must carry to pass.
    /// </summary>
    private static string AdvanceToApprove()
    {
        AdvanceToReview();
        WriteReviewProposal(ValidReadyReviewJson);
        SpecificationTasks.Review(Cmd("review"));
        return SpecificationStore.BundleDigest();
    }

    private static string ValidApprovalJson(string decision, string bundleDigest) =>
        $$"""
        {"decision":"{{decision}}","bundleDigest":"{{bundleDigest}}","rationale":"reviewed and it looks solid","approvedBy":"reviewer@example.com","decidedAt":"2026-01-01T00:00:00Z"}
        """;

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
        // "awaiting_approval"/"approve" is now resumable (see
        // Start_ComRunAguardandoAprovacao_RetomaEmiteApprovePromptEmVezDeReiniciar) — a
        // genuinely non-resumable run needs to actually finish (approve → publish → completed).
        var bundleDigest = AdvanceToApprove();
        WriteApprovalProposal(ValidApprovalJson("approved", bundleDigest));
        SpecificationTasks.Approve(Cmd("approve")); // run reaches "completed" (not resumable)

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

    // --- (e) approve phase: retry-in-place, stale digest, revise routing, publish ---------

    [Fact]
    public void Approve_SemPropostaLegivel_ReemiteComMensagemDeArquivoAusente()
    {
        AdvanceToApprove(); // no approval.proposal.json written

        var result = SpecificationTasks.Approve(Cmd("approve"));

        Assert.Contains("\"value\":\"approve\"", result);
        Assert.Contains(ApprovalProposalPath, result);
        Assert.Equal("approve", SpecificationStore.LoadRun().Phase);
        Assert.Equal("awaiting_approval", SpecificationStore.LoadRun().Status);
    }

    [Fact]
    public void Approve_DecisaoDesconhecida_MantemEmApproveEReportaCodigoEstavel()
    {
        var bundleDigest = AdvanceToApprove();
        WriteApprovalProposal(ValidApprovalJson("maybe", bundleDigest));

        var result = SpecificationTasks.Approve(Cmd("approve"));

        Assert.Contains("\"value\":\"approve\"", result);
        Assert.Contains("APPROVAL_DECISION_INVALID", result);
        Assert.Equal("approve", SpecificationStore.LoadRun().Phase);
    }

    [Fact]
    public void Approve_BundleDigestDesatualizado_EhRejeitadoComErroClaro()
    {
        // Acceptance: approval against a stale digest is rejected. Preview the bundle, then
        // mutate the chain (re-accept the SDD) — the OLD digest carried by the decision no
        // longer matches the current accepted chain.
        var staleDigest = AdvanceToApprove();
        var (sdd, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        // Re-accept the SDD with genuinely different content (not a byte-identical rewrite) —
        // the chain must actually move for the bundle digest to change.
        var mutatedSdd = sdd! with { Adrs = [sdd.Adrs[0] with { Rationale = "Simplicity, revisited" }] };
        SpecificationStore.WriteAccepted(
            SpecificationStore.Phases.Sdd, mutatedSdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        var freshDigest = SpecificationStore.BundleDigest();
        Assert.NotEqual(staleDigest, freshDigest); // the chain really did move

        WriteApprovalProposal(ValidApprovalJson("approved", staleDigest));

        var result = SpecificationTasks.Approve(Cmd("approve"));

        Assert.Contains("\"value\":\"approve\"", result);
        Assert.Contains("APPROVAL_BUNDLE_DIGEST_STALE", result);
        Assert.Equal("approve", SpecificationStore.LoadRun().Phase);
        Assert.Equal("awaiting_approval", SpecificationStore.LoadRun().Status);
    }

    [Fact]
    public void Approve_DecisaoRevise_RoteiaParaReviewSemConsumirRecascade()
    {
        var bundleDigest = AdvanceToApprove();
        WriteApprovalProposal(ValidApprovalJson("revise", bundleDigest));

        var result = SpecificationTasks.Approve(Cmd("approve"));

        Assert.Contains("\"value\":\"review\"", result);
        Assert.Equal("review", SpecificationStore.LoadRun().Phase);
        Assert.Equal("in_progress", SpecificationStore.LoadRun().Status);
        Assert.False(SpecificationStore.LoadRun().Counters.ContainsKey("recascades"));

        // The reviewer can then submit a fresh verdict from "review" as normal.
        WriteReviewProposal(ValidReadyReviewJson);
        var reviewResult = SpecificationTasks.Review(Cmd("review"));
        Assert.Equal("stop", reviewResult);
        Assert.Equal("awaiting_approval", SpecificationStore.LoadRun().Status);
    }

    [Fact]
    public void Approve_DecisaoAprovada_PublicaOBundleEEncerraComoCompleted()
    {
        var bundleDigest = AdvanceToApprove();
        WriteApprovalProposal(ValidApprovalJson("approved", bundleDigest));

        var result = SpecificationTasks.Approve(Cmd("approve"));

        Assert.Equal("stop", result);
        Assert.Equal("completed", SpecificationStore.LoadRun().Status);
        Assert.Equal("stop", SpecificationStore.LoadRun().Phase);

        var activeDir = Path.Combine(SpecsDir, "active");
        Assert.True(File.Exists(Path.Combine(activeDir, "00-prd.md")));
        Assert.True(File.Exists(Path.Combine(activeDir, "10-software-requirements-specification.md")));
        Assert.True(File.Exists(Path.Combine(activeDir, "20-software-design-document.md")));
        Assert.True(File.Exists(Path.Combine(activeDir, "30-readiness-handoff.md")));
    }

    [Fact]
    public void Approve_PublicacaoBloqueadaPorArquivoDesconhecido_EncerraComoPublishBlocked()
    {
        var activeDir = Path.Combine(SpecsDir, "active");
        Directory.CreateDirectory(activeDir);
        File.WriteAllText(Path.Combine(activeDir, "rogue.txt"), "not mine");

        var bundleDigest = AdvanceToApprove();
        WriteApprovalProposal(ValidApprovalJson("approved", bundleDigest));

        var result = SpecificationTasks.Approve(Cmd("approve"));

        Assert.Equal("stop", result);
        Assert.Equal("publish_blocked", SpecificationStore.LoadRun().Status);
        Assert.NotNull(SpecificationStore.LoadRun().TerminalReason);
        Assert.False(File.Exists(Path.Combine(activeDir, "00-prd.md")));
    }

    [Fact]
    public void Approve_PacoteExcedeDocsMaxChars_BloqueiaAntesDePublicarESpecsActiveFicaIntocado()
    {
        const string ConfigPath = "harness.json";
        try
        {
            // A configured docsMaxChars far smaller than any real rendered bundle — this is
            // the "DevelopmentReadinessEvaluator" pre-publish gate (blueprint 0004 §5), which
            // must reject the bundle BEFORE SpecificationPublisher.Publish ever touches
            // specs/active/, using the real configured ceiling (not a hardcoded one).
            File.WriteAllText(ConfigPath, """{"docsMaxChars":10}""");
            HarnessConfig.Reload();

            var bundleDigest = AdvanceToApprove();
            WriteApprovalProposal(ValidApprovalJson("approved", bundleDigest));

            var result = SpecificationTasks.Approve(Cmd("approve"));

            Assert.Equal("stop", result);
            Assert.Equal("publish_blocked", SpecificationStore.LoadRun().Status);
            Assert.Contains("DEV_READINESS_BUNDLE_TOO_LARGE", SpecificationStore.LoadRun().TerminalReason);
            Assert.False(Directory.Exists(Path.Combine(SpecsDir, "active")));
        }
        finally
        {
            if (File.Exists(ConfigPath))
                File.Delete(ConfigPath);
            HarnessConfig.Reload();
        }
    }

    [Fact]
    public void Approve_DecisaoAprovada_PosCondicaoViaDocsReaderConfirmaOBundlePublicado()
    {
        var bundleDigest = AdvanceToApprove();
        WriteApprovalProposal(ValidApprovalJson("approved", bundleDigest));

        var result = SpecificationTasks.Approve(Cmd("approve"));

        Assert.Equal("stop", result);
        Assert.Equal("completed", SpecificationStore.LoadRun().Status);

        // "completed" is only reached because SpecificationPublisher.Publish's internal
        // postcondition (a real DocsReader.Read read-back) already passed — independently
        // re-confirm that read-back here through the same real DocsReader.Read Development uses.
        var (_, files) = DocsReader.Read("specs/active");
        Assert.Equal(
            ["00-prd.md", "10-software-requirements-specification.md", "20-software-design-document.md", "30-readiness-handoff.md"],
            files);
    }

    [Fact]
    public void Start_ComRunAguardandoAprovacao_RetomaEmiteApprovePromptEmVezDeReiniciar()
    {
        var bundleDigest = AdvanceToApprove();

        var result = SpecificationTasks.Start(); // simulates a kill + restart while awaiting approval

        Assert.Contains("\"value\":\"approve\"", result);
        Assert.Contains(ApprovalProposalPath, result);
        Assert.Contains(bundleDigest, result);
        Assert.Equal("approve", SpecificationStore.LoadRun().Phase);
        Assert.Equal("awaiting_approval", SpecificationStore.LoadRun().Status);
        // The already-accepted readiness bundle survives the resume.
        var (verdict, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Readiness, SpecificationJsonContext.Default.ReadinessVerdict);
        Assert.NotNull(verdict);
    }

    // ---- source-document ingestion (blueprint 0004 §2: "ideia curta ou documentos em pasta
    // de fontes") ------------------------------------------------------------------------

    [Fact]
    public void Start_SemPastaDeFontes_EmiteDiscoverSemFontesEIdeiaNasceSemAcervo()
    {
        var result = SpecificationTasks.Start();

        Assert.Contains("\"value\":\"discover\"", result);
        Assert.Contains("No sources folder was found", result);
        var (sources, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Sources, SpecificationJsonContext.Default.SourceBundle);
        Assert.Null(sources);
    }

    [Fact]
    public void Start_ComPastaDeFontes_IngereEReinjetaConteudoNoPromptDeDiscover()
    {
        Directory.CreateDirectory(SourcesDir);
        File.WriteAllText(Path.Combine(SourcesDir, "product-brief.md"), "# Product brief\nTeams lose track of work across too many spreadsheets.");
        File.WriteAllText(Path.Combine(SourcesDir, "call-transcript.md"), "## Call with stakeholder\nWe need visibility into who owns what.");

        var result = SpecificationTasks.Start();

        Assert.Contains("\"value\":\"discover\"", result);
        Assert.Contains("product-brief.md", result);
        Assert.Contains("call-transcript.md", result);
        Assert.Contains("Teams lose track of work", result);
        Assert.Contains("visibility into who owns what", result);

        var (sources, digest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Sources, SpecificationJsonContext.Default.SourceBundle);
        Assert.NotNull(sources);
        Assert.Equal(["call-transcript.md", "product-brief.md"], sources!.Files); // DocsReader orders alphabetically
        Assert.NotNull(digest);
    }

    [Fact]
    public void Start_ComPastaDeFontes_ReinjetaConteudoNoPromptDeDesign()
    {
        Directory.CreateDirectory(SourcesDir);
        File.WriteAllText(
            Path.Combine(SourcesDir, "design.md"),
            "## Architecture\n\n```mermaid\nflowchart LR\n  Client --> API\n```\n\n```text\napp/\n  src/\n```");

        AdvanceToDesign();
        var result = SpecificationTasks.Start();

        Assert.Contains("\"value\":\"design\"", result);
        Assert.Contains("<sources folder=", result);
        Assert.Contains("flowchart LR", result);
        Assert.Contains("app/", result);
        Assert.Contains("sourceDigest", result);
        Assert.Contains("designContent", result);
    }

    [Fact]
    public void Discover_ComFontesIngeridas_RegistraFonteEDigestReaisNaoPlaceholder()
    {
        Directory.CreateDirectory(SourcesDir);
        File.WriteAllText(Path.Combine(SourcesDir, "regulation.md"), "Tasks must be auditable for compliance.");
        SpecificationTasks.Start();

        WriteIdeaProposal(ValidIdeaJson);
        var result = SpecificationTasks.Discover(Cmd("discover"));

        Assert.Contains("\"value\":\"product\"", result); // idea passed IdeaEvaluator, including source/sourceDigest
        var (_, ideaDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        Assert.NotNull(ideaDigest);
    }

    [Fact]
    public void DiscoverRetryPrompt_ReanexaConteudoDeFontesAposFalha()
    {
        Directory.CreateDirectory(SourcesDir);
        File.WriteAllText(Path.Combine(SourcesDir, "brief.md"), "Teams need a shared task board.");
        SpecificationTasks.Start();

        // Invalid: missing required fields (title/problem/users/desiredOutcomes) — must retry
        // in place while still showing the ingested source material, not silently dropping it.
        WriteIdeaProposal("""{"schema":"iao/idea/v1","title":"","problem":"","users":[],"desiredOutcomes":[],"constraints":[],"openQuestions":[]}""");

        var result = SpecificationTasks.Discover(Cmd("discover"));

        Assert.Contains("\"value\":\"discover\"", result);
        Assert.Contains("brief.md", result);
        Assert.Contains("Teams need a shared task board", result);
    }

    [Fact]
    public void Start_FrescoAposRunAnterior_ReingereFontesEmVezDeReaproveitarAcervoAntigo()
    {
        Directory.CreateDirectory(SourcesDir);
        File.WriteAllText(Path.Combine(SourcesDir, "brief.md"), "First run material.");
        SpecificationTasks.Start();
        WriteIdeaProposal(ValidIdeaJson);
        SpecificationTasks.Discover(Cmd("discover")); // advances to product, "in_progress"

        // A brand new run only starts once the previous one reached a terminal state — force
        // that here the same way a completed/needs_human_decision run would, then change the
        // sources on disk and start again.
        SpecificationStore.SaveRun(SpecificationStore.LoadRun() with { Status = "completed", Phase = "stop" });
        File.WriteAllText(Path.Combine(SourcesDir, "brief.md"), "Second run material, replacing the first.");

        var result = SpecificationTasks.Start();

        Assert.Contains("Second run material", result);
        Assert.DoesNotContain("First run material", result);
    }
}
