using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// O DocsReader é a entrada alternativa ao input interativo: lê os documentos da pasta
/// (determinístico, em código) para que o modelo só precise sintetizar o brief.
/// </summary>
public class DocsReaderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "docsreader-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void HasDocs_PastaInexistente_False()
    {
        Assert.False(DocsReader.HasDocs(_dir));
    }

    [Fact]
    public void HasDocs_PastaVazia_False()
    {
        Directory.CreateDirectory(_dir);

        Assert.False(DocsReader.HasDocs(_dir));
    }

    [Fact]
    public void HasDocs_IgnoraExtensoesNaoSuportadas()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "imagem.png"), "x");
        File.WriteAllText(Path.Combine(_dir, "dados.json"), "{}");

        Assert.False(DocsReader.HasDocs(_dir));
    }

    [Fact]
    public void HasDocs_ComMarkdown_True()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "spec.md"), "conteúdo");

        Assert.True(DocsReader.HasDocs(_dir));
    }

    [Fact]
    public void Read_ConcatenaMdETxtEmOrdemAlfabetica()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "b-notas.txt"), "notas");
        File.WriteAllText(Path.Combine(_dir, "a-spec.md"), "spec");

        var (content, files) = DocsReader.Read(_dir);

        Assert.Equal(["a-spec.md", "b-notas.txt"], files);
        Assert.Contains("## a-spec.md", content);
        Assert.Contains("## b-notas.txt", content);
        Assert.True(
            content.IndexOf("a-spec.md", StringComparison.Ordinal)
            < content.IndexOf("b-notas.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_PastaInexistente_VazioSemFontes()
    {
        var (content, files) = DocsReader.Read(_dir);

        Assert.Equal(string.Empty, content);
        Assert.Empty(files);
    }
}
