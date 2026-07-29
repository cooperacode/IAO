namespace Harness.Engine.Tests;

/// <summary>
/// Cost ceiling (Phase 2): the accumulated emitted-instruction chars — the only measure
/// the engine can attest on its own — cuts off the run when it exceeds the ceiling. Off
/// (0) by default — only the step ceiling applies.
/// </summary>
public class CostBudgetTests : IDisposable
{
    private const string ConfigPath = "harness.json";

    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["start"] = _ => "PROMPT_START",
        ["classify"] = _ => "PROMPT_CLASSIFY_0123456789", // 25 chars per turn
    };

    public CostBudgetTests() => Clean();
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
    public void Dispatch_ProxyDeChars_CortaQuandoOAcumuladoExcede()
    {
        Configure("""{"maxInstructionChars":30}""");

        // 1st turn: accumulated 0 → passes; emits 25 chars.
        var first = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        Assert.NotEqual("stop", first);

        // 2nd turn: accumulated 25 → passes; emits 25 more (total 50).
        var second = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        Assert.NotEqual("stop", second);

        // 3rd turn: accumulated 50 > 30 → cut off by budget.
        var third = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        Assert.Equal("stop", third);

        Assert.Equal(TraceOutcome.Budget, Trace.Load()[^1].Outcome);
    }

    [Fact]
    public void Dispatch_SemTetoConfigurado_NaoCortaPorCusto()
    {
        // Default: maxInstructionChars=0 → only the step ceiling governs.
        for (var i = 0; i < 5; i++)
        {
            var result = TaskRegistry.Dispatch(
                ["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
            Assert.NotEqual("stop", result);
        }
    }

    [Fact]
    public void Dispatch_Start_ZeraOCustoAcumulado()
    {
        Configure("""{"maxInstructionChars":30}""");

        TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);

        // New workflow: reset zeros out CostChars along with Step.
        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks);

        Assert.NotEqual("stop", result);
        // The reset zeros the accumulator, leaving only the instruction emitted by start itself.
        Assert.Equal("PROMPT_START".Length, StateStore.Load().CostChars);
    }
}
