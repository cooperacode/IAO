namespace Harness.Engine.Tests;

/// <summary>
/// Config externa (`harness.json`): ausente ou inválida NUNCA derruba o run — cai nos
/// defaults; parcial preenche só o que veio (zero = desligado apenas nos tetos de custo).
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
        HarnessConfig.Reload();
    }

    [Fact]
    public void Load_SemArquivo_UsaDefaults()
    {
        var config = HarnessConfig.Load();

        Assert.Equal(HarnessConfig.Default, config);
        Assert.Equal(12, config.MaxSteps);
        Assert.Equal(0, config.MaxInstructionChars); // teto de custo desligado por padrão
        Assert.Equal(0, config.TimeoutMs);           // guarda de tempo desligada por padrão
    }

    [Fact]
    public void Load_ComTimeout_LeENormaliza()
    {
        File.WriteAllText(ConfigPath, """{"timeoutMs":30000}""");

        Assert.Equal(30000, HarnessConfig.Load().TimeoutMs);

        // Valor negativo é normalizado para 0 (desligado), como o teto de custo.
        File.WriteAllText(ConfigPath, """{"timeoutMs":-5}""");
        Assert.Equal(0, HarnessConfig.Load().TimeoutMs);
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
        File.WriteAllText(ConfigPath, "{ isso não é json ");

        var config = HarnessConfig.Load();

        Assert.Equal(HarnessConfig.Default, config);
    }
}
