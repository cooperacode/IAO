using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// PathResolver é a única porta de entrada para docs/skills relativos ao CWD; um symlink
/// que desvia o alvo para fora da base autorizada é o cenário adversarial que o
/// containment (RFC §6.3) precisa fechar sem quebrar o uso legítimo (arquivo comum, dentro
/// da mesma pasta).
/// </summary>
public class PathResolverTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "pathresolver-" + Guid.NewGuid().ToString("N"));

    private readonly string _originalCwd = Directory.GetCurrentDirectory();

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Resolve_CaminhoAbsoluto_DevolveOMesmoCaminho()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(file, PathResolver.Resolve(file));
    }

    [Fact]
    public void Resolve_ArquivoComumDentroDoCwd_Resolve()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "conteúdo");
        Directory.SetCurrentDirectory(_dir);

        var resolved = PathResolver.Resolve("a.txt");

        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void Resolve_SymlinkQueEscapaDoCwd_NaoSeguoLink()
    {
        if (OperatingSystem.IsWindows())
            return; // criar symlink no CI Windows exige privilégio elevado; cenário coberto nos outros dois engines.

        var workDir = Path.Combine(_dir, "work");
        var outsideDir = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(outsideDir);

        var secretFile = Path.Combine(outsideDir, "secret.txt");
        File.WriteAllText(secretFile, "fora da base autorizada");

        var linkPath = Path.Combine(workDir, "desviado.txt");
        File.CreateSymbolicLink(linkPath, secretFile);

        Directory.SetCurrentDirectory(workDir);

        var resolved = PathResolver.Resolve("desviado.txt");

        // Nunca deve devolver o alvo real fora da base — mesmo que o link exista e aponte
        // para um arquivo válido, o containment tem que barrar o desvio.
        Assert.NotEqual(Path.GetFullPath(secretFile), Path.GetFullPath(resolved));
    }
}
