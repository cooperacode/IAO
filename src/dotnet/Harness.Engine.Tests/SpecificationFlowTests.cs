using Flows.Specification;
using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// Specification flow happy path (blueprint 0004 §2): start → discover → product → stop.
/// Covers resume-from-persisted-phase on a fresh "start" (kill + restart) and the retry-in-
/// place behavior of discover/product on evaluator failure.
/// </summary>
public class SpecificationFlowTests : IDisposable
{
    private const string IdeaProposalPath = ".harness/specification/active/idea.proposal.json";
    private const string PrdProposalPath = ".harness/specification/active/prd.proposal.json";

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
        if (File.Exists(IdeaProposalPath))
            File.Delete(IdeaProposalPath);
        if (File.Exists(PrdProposalPath))
            File.Delete(PrdProposalPath);
    }

    private static void WriteIdeaProposal(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IdeaProposalPath)!);
        File.WriteAllText(IdeaProposalPath, json);
    }

    private static void WritePrdProposal(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PrdProposalPath)!);
        File.WriteAllText(PrdProposalPath, json);
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

    // --- (a) happy path -----------------------------------------------------------------

    [Fact]
    public void HappyPath_StartDiscoverProduct_FechaComPrdAceito()
    {
        var ideaDigest = AdvanceToProduct();
        WritePrdProposal(ValidPrdJson(ideaDigest));

        var result = SpecificationTasks.Product(Cmd("product"));

        Assert.Equal("stop", result);
        var (prd, prdDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        Assert.NotNull(prd);
        Assert.NotNull(prdDigest);
        Assert.Equal("Give every team visibility", prd!.Vision);
        Assert.Equal("completed", SpecificationStore.LoadRun().Status);
        Assert.Equal("stop", SpecificationStore.LoadRun().Phase);
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
    public void Start_SemRunEmProgresso_ReiniciaDoDiscoverEDescartaAceitosAnteriores()
    {
        var ideaDigest = AdvanceToProduct();
        WritePrdProposal(ValidPrdJson(ideaDigest));
        SpecificationTasks.Product(Cmd("product")); // run completed

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
}
