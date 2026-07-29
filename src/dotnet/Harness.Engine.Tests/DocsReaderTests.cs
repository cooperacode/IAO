using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// DocsReader is the alternative input to the interactive one: it reads the folder's
/// documents (deterministic, in code) so the model only needs to synthesize the brief.
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
        File.WriteAllText(Path.Combine(_dir, "image.png"), "x");
        File.WriteAllText(Path.Combine(_dir, "data.json"), "{}");

        Assert.False(DocsReader.HasDocs(_dir));
    }

    [Fact]
    public void HasDocs_ComMarkdown_True()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "spec.md"), "content");

        Assert.True(DocsReader.HasDocs(_dir));
    }

    [Fact]
    public void Read_ConcatenaMdETxtEmOrdemAlfabetica()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "b-notas.txt"), "notes");
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

    [Fact]
    public void Read_ConteudoComAcentoEEmoji_NaoQuebraCaractereMultibyte()
    {
        // "café ☕" has "é" (2 bytes) and "☕" (3 bytes) in UTF-8 — a naive cut by byte
        // position in the middle of either would produce invalid bytes.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.md"), "café ☕ café ☕ café ☕");

        var (content, _) = DocsReader.Read(_dir);

        Assert.Contains("café ☕", content);
        // If the content survived the roundtrip as a valid .NET string, the cut (when
        // applied) already respected the UTF-8 boundary — an invalid string would have
        // turned into U+FFFD.
        Assert.DoesNotContain('�', content);
    }
}
