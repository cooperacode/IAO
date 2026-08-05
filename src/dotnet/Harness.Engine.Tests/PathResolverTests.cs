using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// PathResolver is the sole entry point for specs/skills relative to the CWD; a symlink
/// that steers the target outside the authorized base is the adversarial scenario the
/// containment (RFC §6.3) needs to close without breaking legitimate use (a regular file,
/// inside the same folder).
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
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "content");
        Directory.SetCurrentDirectory(_dir);

        var resolved = PathResolver.Resolve("a.txt");

        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void Resolve_SymlinkQueEscapaDoCwd_NaoSeguoLink()
    {
        if (OperatingSystem.IsWindows())
            return; // creating a symlink on Windows CI requires elevated privileges; scenario covered by the other two engines.

        var workDir = Path.Combine(_dir, "work");
        var outsideDir = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(outsideDir);

        var secretFile = Path.Combine(outsideDir, "secret.txt");
        File.WriteAllText(secretFile, "outside the authorized base");

        var linkPath = Path.Combine(workDir, "diverted.txt");
        File.CreateSymbolicLink(linkPath, secretFile);

        Directory.SetCurrentDirectory(workDir);

        var resolved = PathResolver.Resolve("diverted.txt");

        // Must never return the real target outside the base — even if the link exists
        // and points to a valid file, containment has to block the diversion.
        Assert.NotEqual(Path.GetFullPath(secretFile), Path.GetFullPath(resolved));
    }
}
