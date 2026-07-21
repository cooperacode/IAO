namespace Harness.Engine.Tests;

/// <summary>
/// Artefatos separados por arquivo + manifesto: a ordem de gravação é a ordem de leitura
/// (o juiz recebe as seções na sequência do flow), e o template dá a forma sem código.
/// </summary>
public class ArtifactStoreTests : IDisposable
{
    public ArtifactStoreTests() => ArtifactStore.Reset();
    public void Dispose() => ArtifactStore.Reset();

    [Fact]
    public void Write_GravaOArquivoERegistraNoManifesto()
    {
        var path = ArtifactStore.Write("historias", "# Histórias\n\n1. a");

        Assert.True(File.Exists(path));
        Assert.Equal([path], ArtifactStore.Files());
    }

    [Fact]
    public void Write_MesmoNomeDuasVezes_SobrescreveSemDuplicarNoManifesto()
    {
        ArtifactStore.Write("historias", "v1");
        var path = ArtifactStore.Write("historias", "v2");

        Assert.Single(ArtifactStore.Files());
        Assert.Equal("v2", File.ReadAllText(path));
    }

    [Fact]
    public void ReadAll_ConcatenaNaOrdemDeGravacao()
    {
        ArtifactStore.Write("item", "# Item");
        ArtifactStore.Write("historias", "# Histórias");

        var all = ArtifactStore.ReadAll();

        Assert.True(all.IndexOf("# Item", StringComparison.Ordinal) < all.IndexOf("# Histórias", StringComparison.Ordinal));
    }

    [Fact]
    public void Reset_ApagaArtefatosEManifesto()
    {
        var path = ArtifactStore.Write("historias", "x");

        ArtifactStore.Reset();

        Assert.False(File.Exists(path));
        Assert.False(ArtifactStore.HasArtifacts());
        Assert.Empty(ArtifactStore.Files());
    }

    [Fact]
    public void Render_SubstituiPlaceholdersEMantemOsDesconhecidos()
    {
        var result = ArtifactTemplate.Render(
            "# {{titulo}}\n\n{{corpo}}\n\n{{sem_valor}}",
            new Dictionary<string, string> { ["titulo"] = "Riscos", ["corpo"] = "lista" });

        Assert.Contains("# Riscos", result);
        Assert.Contains("lista", result);
        Assert.Contains("{{sem_valor}}", result); // dado faltante fica visível, não some
    }
}
