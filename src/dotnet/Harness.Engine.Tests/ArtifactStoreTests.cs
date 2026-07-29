namespace Harness.Engine.Tests;

/// <summary>
/// Artifacts split by file + manifest: write order is read order (the judge receives the
/// sections in the flow's sequence), and the template gives the shape without code.
/// </summary>
public class ArtifactStoreTests : IDisposable
{
    public ArtifactStoreTests() => ArtifactStore.Reset();
    public void Dispose() => ArtifactStore.Reset();

    [Fact]
    public void Write_GravaOArquivoERegistraNoManifesto()
    {
        var path = ArtifactStore.Write("stories", "# Stories\n\n1. a");

        Assert.True(File.Exists(path));
        Assert.Equal([path], ArtifactStore.Files());
    }

    [Fact]
    public void Write_MesmoNomeDuasVezes_SobrescreveSemDuplicarNoManifesto()
    {
        ArtifactStore.Write("stories", "v1");
        var path = ArtifactStore.Write("stories", "v2");

        Assert.Single(ArtifactStore.Files());
        Assert.Equal("v2", File.ReadAllText(path));
    }

    [Fact]
    public void ReadAll_ConcatenaNaOrdemDeGravacao()
    {
        ArtifactStore.Write("item", "# Item");
        ArtifactStore.Write("stories", "# Stories");

        var all = ArtifactStore.ReadAll();

        Assert.True(all.IndexOf("# Item", StringComparison.Ordinal) < all.IndexOf("# Stories", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_ArtefatoExistente_DevolveOConteudo()
    {
        ArtifactStore.Write("brief", "# Brief\n\nBuild X.");

        Assert.Equal("# Brief\n\nBuild X.", ArtifactStore.Read("brief"));
    }

    [Fact]
    public void Read_ArtefatoAusente_DevolveVazio()
    {
        Assert.Equal("", ArtifactStore.Read("never-written"));
    }

    [Fact]
    public void Reset_ApagaArtefatosEManifesto()
    {
        var path = ArtifactStore.Write("stories", "x");

        ArtifactStore.Reset();

        Assert.False(File.Exists(path));
        Assert.False(ArtifactStore.HasArtifacts());
        Assert.Empty(ArtifactStore.Files());
    }

    [Fact]
    public void Render_SubstituiPlaceholdersEMantemOsDesconhecidos()
    {
        var result = ArtifactTemplate.Render(
            "# {{title}}\n\n{{body}}\n\n{{no_value}}",
            new Dictionary<string, string> { ["title"] = "Risks", ["body"] = "list" });

        Assert.Contains("# Risks", result);
        Assert.Contains("list", result);
        Assert.Contains("{{no_value}}", result); // missing data stays visible, doesn't disappear
    }
}
