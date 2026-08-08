using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// verify_cmd/target_dir live outside state.json on purpose: they need to survive the
/// unconditional reset TaskRegistry.Dispatch does to state.json on every "start", so a
/// resumed run (pending feature) still works in smoke/verify without needing a new "plan".
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
    public void WriteELoad_PreservamORunId()
    {
        RunConfigStore.Write(new RunConfig("npm test", "app", "019b1ed0-6bea-7bc1-a790-0bdb42bb8ab6"));

        var loaded = RunConfigStore.Load();

        Assert.Equal("019b1ed0-6bea-7bc1-a790-0bdb42bb8ab6", loaded.RunId);
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
        RunConfigStore.Reset(); // no-op, must not throw
    }

    [Fact]
    public void WriteELoad_FazemRoundtripComVerifyCmds()
    {
        RunConfigStore.Write(new RunConfig("npm test", "app", VerifyCmds: ["npm run lint", "npm run typecheck"]));

        var loaded = RunConfigStore.Load();

        Assert.NotNull(loaded.VerifyCmds);
        Assert.Equal(["npm run lint", "npm run typecheck"], loaded.VerifyCmds);
    }

    [Fact]
    public void Load_RunConfigLegadoSemVerifyCmds_CarregaComNull()
    {
        // Simulates a run_config.json written by an earlier harness version, without the
        // "verifyCmds" key — proves the single-command path stays the default.
        Directory.CreateDirectory(".harness");
        File.WriteAllText(".harness/run_config.json",
            """{"verifyCmd":"npm test","targetDir":"app","runId":""}""");

        var loaded = RunConfigStore.Load();

        Assert.Equal("npm test", loaded.VerifyCmd);
        Assert.Null(loaded.VerifyCmds);
    }
}
