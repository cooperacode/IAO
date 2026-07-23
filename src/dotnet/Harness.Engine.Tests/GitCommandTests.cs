using Harness.Engine;

namespace Harness.Engine.Tests;

public class GitCommandTests : IDisposable
{
    private readonly string _tempDir;

    public GitCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Run_ComandoValido_CapturaStdout()
    {
        var result = GitCommand.Run(_tempDir, "--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git version", result.Output);
    }

    [Fact]
    public void Run_DiretorioInexistente_RetornaErroSemLancar()
    {
        var missing = Path.Combine(_tempDir, "missing");

        var result = GitCommand.Run(missing, "status");

        Assert.Equal(-1, result.ExitCode);
        Assert.NotEmpty(result.Error);
    }
}
