using Flows.Specification;

namespace Harness.Engine.Tests;

/// <summary>
/// SpecificationStore is the namespaced persistence layer for the Specification flow's own
/// artifacts (source: specs/0004-blueprint-prd-dotnet.html §4) — separate from
/// Harness.Engine.ArtifactStore so Development's own reset-on-start never wipes it.
/// </summary>
public class SpecificationStoreTests : IDisposable
{
    public SpecificationStoreTests() => SpecificationStore.Reset();
    public void Dispose() => SpecificationStore.Reset();

    [Fact]
    public void WriteAcceptedELerAccepted_FazemRoundtripEDigestBate()
    {
        var prd = new PrdDocument(
            "iao/prd/v1",
            "sha256:idea",
            "vision",
            [new Goal("OBJ-001", "outcome")],
            [new Metric("MET-001", "OBJ-001", "measure", "target")],
            ["non-goal"],
            ["scope"],
            [],
            [],
            []);

        var writtenDigest = SpecificationStore.WriteAccepted(SpecificationStore.Phases.Prd, prd, SpecificationJsonContext.Default.PrdDocument);

        var (loaded, readDigest) = SpecificationStore.ReadAccepted(SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);

        Assert.NotNull(loaded);
        Assert.Equal(prd.Vision, loaded!.Vision);
        Assert.Equal(writtenDigest, readDigest);
        Assert.StartsWith("sha256:", writtenDigest);
    }

    [Fact]
    public void MesmoConteudo_ProduzMesmoDigestEmDuasEscritas()
    {
        var idea = new IdeaFrame("iao/idea/v1", "title", "problem", ["u1"], ["outcome"], ["constraint"], []);

        var first = SpecificationStore.WriteAccepted(SpecificationStore.Phases.Idea, idea, SpecificationJsonContext.Default.IdeaFrame);
        SpecificationStore.Reset();
        var second = SpecificationStore.WriteAccepted(SpecificationStore.Phases.Idea, idea, SpecificationJsonContext.Default.IdeaFrame);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DigestOf_BateComDigestDoAccepted()
    {
        var idea = new IdeaFrame("iao/idea/v1", "title", "problem", [], [], [], []);

        var expected = SpecificationStore.DigestOf(idea, SpecificationJsonContext.Default.IdeaFrame);
        var written = SpecificationStore.WriteAccepted(SpecificationStore.Phases.Idea, idea, SpecificationJsonContext.Default.IdeaFrame);

        Assert.Equal(expected, written);
    }

    [Fact]
    public void Proposal_NaoAfetaAccepted()
    {
        var accepted = new IdeaFrame("iao/idea/v1", "accepted-title", "problem", [], [], [], []);
        var proposal = new IdeaFrame("iao/idea/v1", "proposal-title", "problem", [], [], [], []);

        SpecificationStore.WriteAccepted(SpecificationStore.Phases.Idea, accepted, SpecificationJsonContext.Default.IdeaFrame);
        SpecificationStore.WriteProposal(SpecificationStore.Phases.Idea, proposal, SpecificationJsonContext.Default.IdeaFrame);

        var (loadedAccepted, _) = SpecificationStore.ReadAccepted(SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        var loadedProposal = SpecificationStore.ReadProposal(SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);

        Assert.Equal("accepted-title", loadedAccepted!.Title);
        Assert.Equal("proposal-title", loadedProposal!.Title);
    }

    [Fact]
    public void ReadAccepted_SemArquivo_RetornaNuloSemLancar()
    {
        var (value, digest) = SpecificationStore.ReadAccepted(SpecificationStore.Phases.Sdd, SpecificationJsonContext.Default.SoftwareSpecification);

        Assert.Null(value);
        Assert.Null(digest);
    }

    [Fact]
    public void RunState_RoundtripPersisteFases()
    {
        var state = new RunState(3, "in_progress", "product", new Dictionary<string, int> { ["recascades"] = 1 }, "label", null);

        SpecificationStore.SaveRun(state);
        var loaded = SpecificationStore.LoadRun();

        Assert.Equal(3, loaded.Step);
        Assert.Equal("product", loaded.Phase);
        Assert.Equal(1, loaded.Counters["recascades"]);
    }

    [Fact]
    public void LoadRun_SemArquivo_RetornaEstadoInicialDeStart()
    {
        var loaded = SpecificationStore.LoadRun();

        Assert.Equal(0, loaded.Step);
        Assert.Equal("start", loaded.Phase);
    }

    [Fact]
    public void Reset_RemoveArquivosPersistidos()
    {
        SpecificationStore.SaveRun(new RunState(1, "in_progress", "discover", new(), null, null));
        SpecificationStore.WriteAccepted(SpecificationStore.Phases.Idea, new IdeaFrame("s", "t", "p", [], [], [], []), SpecificationJsonContext.Default.IdeaFrame);

        SpecificationStore.Reset();

        var loaded = SpecificationStore.LoadRun();
        Assert.Equal("start", loaded.Phase);
        var (value, _) = SpecificationStore.ReadAccepted(SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        Assert.Null(value);
    }
}
