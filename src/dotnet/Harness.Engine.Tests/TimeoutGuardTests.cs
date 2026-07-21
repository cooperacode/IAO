namespace Harness.Engine.Tests;

/// <summary>
/// Guarda de tempo por passo: uma task que trava (loop infinito na lógica de domínio) é
/// cortada ao exceder o teto — diagnóstico no stderr + "stop" no stdout, desfecho "timeout"
/// no trace. Desligada (0) por padrão; ligada via harness.json.
/// </summary>
public class TimeoutGuardTests : IDisposable
{
    private const string ConfigPath = "harness.json";

    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["fast"] = _ => "PROMPT_FAST",
        ["slow"] = _ => { Thread.Sleep(500); return "PROMPT_SLOW"; },
    };

    public TimeoutGuardTests() => Clean();
    public void Dispose() => Clean();

    private static void Clean()
    {
        StateStore.Reset();
        Trace.Reset();
        if (File.Exists(ConfigPath))
            File.Delete(ConfigPath);
        HarnessConfig.Reload();
    }

    private static void Configure(string json)
    {
        File.WriteAllText(ConfigPath, json);
        HarnessConfig.Reload();
    }

    [Fact]
    public void Dispatch_TaskLentaAlemDoTeto_CortaComTimeout()
    {
        Configure("""{"timeoutMs":50}""");

        var result = TaskRegistry.Dispatch(["""{"type":"tool","value":"slow"}"""], Tasks);

        Assert.Equal("stop", result);
        Assert.Equal(TraceOutcome.Timeout, Trace.Load()[^1].Outcome);
    }

    [Fact]
    public void Dispatch_TaskRapidaDentroDoTeto_ExecutaNormalmente()
    {
        Configure("""{"timeoutMs":50}""");

        var result = TaskRegistry.Dispatch(["""{"type":"tool","value":"fast"}"""], Tasks);

        Assert.Equal("PROMPT_FAST", result);
        Assert.Equal(TraceOutcome.Instruction, Trace.Load()[^1].Outcome);
    }

    [Fact]
    public void Dispatch_SemTetoConfigurado_NaoCortaTaskLenta()
    {
        // Default: timeoutMs=0 → guarda desligada; a task lenta roda até o fim.
        var result = TaskRegistry.Dispatch(["""{"type":"tool","value":"slow"}"""], Tasks);

        Assert.Equal("PROMPT_SLOW", result);
        Assert.NotEqual("stop", result);
    }
}
