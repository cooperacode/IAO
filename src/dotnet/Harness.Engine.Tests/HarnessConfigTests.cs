namespace Harness.Engine.Tests;

/// <summary>
/// External config (`harness.json`): missing or invalid NEVER brings down the run — falls
/// back to defaults; partial only fills in what came in (zero disables only cost ceilings;
/// the timeout remains enabled).
/// </summary>
public class HarnessConfigTests : IDisposable
{
    private const string ConfigPath = "harness.json";

    public HarnessConfigTests() => Clean();
    public void Dispose() => Clean();

    private static void Clean()
    {
        if (File.Exists(ConfigPath))
            File.Delete(ConfigPath);
        Environment.SetEnvironmentVariable("HARNESS_TIMEOUT_MS", null);
        HarnessConfig.Reload();
    }

    [Fact]
    public void Load_SemArquivo_UsaDefaults()
    {
        var config = HarnessConfig.Load();

        Assert.Equal(HarnessConfig.Default, config);
        Assert.Equal(12, config.MaxSteps);
        Assert.Equal(0, config.MaxInstructionChars); // cost ceiling disabled by default
        Assert.Equal(10 * 60_000, config.TimeoutMs);       // time guard is always enabled
    }

    [Fact]
    public void Load_ComTimeout_LeENormaliza()
    {
        File.WriteAllText(ConfigPath, """{"timeoutMs":30000}""");

        Assert.Equal(30000, HarnessConfig.Load().TimeoutMs);

        // A negative value falls back to the enabled default; timeout cannot be disabled.
        File.WriteAllText(ConfigPath, """{"timeoutMs":-5}""");
        Assert.Equal(10 * 60_000, HarnessConfig.Load().TimeoutMs);
    }

    [Fact]
    public void Load_ComArquivo_UsaOsValoresDoArquivo()
    {
        File.WriteAllText(ConfigPath,
            """{"maxSteps":5,"maxInstructionChars":20000,"docsMaxChars":10000,"docsFolder":"specs"}""");

        var config = HarnessConfig.Load();

        Assert.Equal(5, config.MaxSteps);
        Assert.Equal(20000, config.MaxInstructionChars);
        Assert.Equal(10000, config.DocsMaxChars);
        Assert.Equal("specs", config.DocsFolder);
    }

    [Fact]
    public void Load_ArquivoParcial_CompletaComDefaults()
    {
        File.WriteAllText(ConfigPath, """{"maxInstructionChars":8000}""");

        var config = HarnessConfig.Load();

        Assert.Equal(8000, config.MaxInstructionChars);
        Assert.Equal(HarnessConfig.Default.MaxSteps, config.MaxSteps);
        Assert.Equal(HarnessConfig.Default.DocsMaxChars, config.DocsMaxChars);
        Assert.Equal(HarnessConfig.Default.DocsFolder, config.DocsFolder);
    }

    [Fact]
    public void Load_ArquivoInvalido_CaiNosDefaultsSemLancar()
    {
        File.WriteAllText(ConfigPath, "{ this is not json ");

        var config = HarnessConfig.Load();

        Assert.Equal(HarnessConfig.Default, config);
    }

    [Fact]
    public void Load_TimeoutAcimaDoTeto_ClampaNoMaximoPermitido()
    {
        // harness.json lives in the supervised agent's working directory: even if it
        // edits the file to grant itself a huge timeout, the hard ceiling prevails.
        File.WriteAllText(ConfigPath, """{"timeoutMs":99999999}""");

        Assert.Equal(10 * 60_000, HarnessConfig.Load().TimeoutMs);
    }

    [Fact]
    public void Load_ComEnvVar_SobrepoeOTimeoutDoArquivo()
    {
        File.WriteAllText(ConfigPath, """{"timeoutMs":1000}""");
        Environment.SetEnvironmentVariable("HARNESS_TIMEOUT_MS", "2000");

        Assert.Equal(2000, HarnessConfig.Load().TimeoutMs);
    }

    [Fact]
    public void Load_EnvVarTambemRespeitaOTeto()
    {
        Environment.SetEnvironmentVariable("HARNESS_TIMEOUT_MS", "99999999");

        Assert.Equal(10 * 60_000, HarnessConfig.Load().TimeoutMs);
    }

    [Fact]
    public void Load_EnvVarInvalida_EIgnorada()
    {
        File.WriteAllText(ConfigPath, """{"timeoutMs":1000}""");
        Environment.SetEnvironmentVariable("HARNESS_TIMEOUT_MS", "not a number");

        Assert.Equal(1000, HarnessConfig.Load().TimeoutMs);
    }
}
