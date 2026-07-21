using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// verify_cmd/target_dir vivem fora de state.json de propósito: precisam sobreviver ao reset
/// incondicional que TaskRegistry.Dispatch faz em state.json a cada "start", para que um run
/// retomado (feature pendente) ainda funcione em smoke/verify sem precisar de um novo "plan".
/// </summary>
public class RunConfigStoreTests : IDisposable
{
    public RunConfigStoreTests() => RunConfigStore.Reset();
    public void Dispose() => RunConfigStore.Reset();

    [Fact]
    public void WriteELoad_FazemRoundtrip()
    {
        RunConfigStore.Write(new RunConfig("npm test", "app"));

        var loaded = RunConfigStore.Load();

        Assert.Equal("npm test", loaded.VerifyCmd);
        Assert.Equal("app", loaded.TargetDir);
    }

    [Fact]
    public void Load_ArquivoAusente_RetornaDefaultsSemLancar()
    {
        var loaded = RunConfigStore.Load();

        Assert.Equal("", loaded.VerifyCmd);
        Assert.Equal(".", loaded.TargetDir);
    }

    [Fact]
    public void Reset_ApagaOArquivo()
    {
        RunConfigStore.Write(new RunConfig("npm test", "app"));

        RunConfigStore.Reset();

        Assert.Equal(new RunConfig(), RunConfigStore.Load());
    }

    [Fact]
    public void Reset_SemArquivo_NaoLanca()
    {
        RunConfigStore.Reset(); // no-op, não deve lançar
    }
}
