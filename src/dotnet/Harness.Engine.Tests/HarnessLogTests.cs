namespace Harness.Engine.Tests;

/// <summary>
/// harness.log is the persisted, human-readable counterpart to what used to be
/// stderr-only diagnostics, plus the step entry/exit markers that make an in-flight step
/// observable before it completes (see TaskRegistryTests for the entry-before-action and
/// fault-logging regressions).
/// </summary>
public class HarnessLogTests : IDisposable
{
    private const string FilePath = ".harness/harness.log";

    public HarnessLogTests() => Clean();
    public void Dispose() => Clean();

    private static void Clean()
    {
        HarnessLog.Reset();
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    [Fact]
    public void Info_Grava_UmaLinhaComNivelEMensagem()
    {
        HarnessLog.Info("[step 1] enter 'start'");

        var line = Assert.Single(File.ReadAllLines(FilePath));
        Assert.Contains("[INFO]", line);
        Assert.Contains("[step 1] enter 'start'", line);
    }

    [Fact]
    public void Error_GravaNoArquivo_AlemDeStderr()
    {
        HarnessLog.Error("[harness] something failed");

        var line = Assert.Single(File.ReadAllLines(FilePath));
        Assert.Contains("[ERROR]", line);
        Assert.Contains("[harness] something failed", line);
    }

    [Fact]
    public void Reset_ApagaOArquivo()
    {
        HarnessLog.Info("first run");
        Assert.True(File.Exists(FilePath));

        HarnessLog.Reset();

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void SemArquivoAinda_ResetNaoLancaExcecao()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);

        var ex = Record.Exception(HarnessLog.Reset);

        Assert.Null(ex);
    }
}
