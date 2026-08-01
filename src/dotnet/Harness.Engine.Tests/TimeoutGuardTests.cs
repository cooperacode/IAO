namespace Harness.Engine.Tests;

/// <summary>
/// Per-step time guard: a task that hangs (infinite loop in domain logic) is cut off when
/// it exceeds the ceiling — stderr diagnostic + "stop" on stdout, "timeout" outcome in the
/// trace. Off (0) by default; enabled via harness.json.
/// </summary>
public class TimeoutGuardTests : IDisposable
{
    private const string ConfigPath = "harness.json";

    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["start"] = _ => "PROMPT_START",
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

        // Non-start commands remain terminal even if the workspace config is changed.
        File.WriteAllText(ConfigPath, """{"timeoutMs":0}""");
        HarnessConfig.Reload();
        Assert.Equal("stop", TaskRegistry.Dispatch(["""{"type":"tool","value":"fast"}"""], Tasks));

        // An explicit start clears only the recoverable timeout latch.
        Assert.Equal("PROMPT_START", TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks));
        Assert.Null(StateStore.TerminalReason());
        Assert.Equal("PROMPT_FAST", TaskRegistry.Dispatch(["""{"type":"tool","value":"fast"}"""], Tasks));
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
        // Default timeout is enabled, but this task completes within the 30-second default.
        var result = TaskRegistry.Dispatch(["""{"type":"tool","value":"slow"}"""], Tasks);

        Assert.Equal("PROMPT_SLOW", result);
        Assert.NotEqual("stop", result);
    }
}
