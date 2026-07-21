namespace Harness.Engine.Tests;

/// <summary>
/// Teto de custo (Fase 2): o acumulado de chars de instrução emitida — a única medida que
/// a engine atesta sozinha — corta o run quando excede o teto. Desligado (0) por padrão —
/// só o teto de passos vale.
/// </summary>
public class CostBudgetTests : IDisposable
{
    private const string ConfigPath = "harness.json";

    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["start"] = _ => "PROMPT_START",
        ["classify"] = _ => "PROMPT_CLASSIFY_0123456789", // 25 chars por turno
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

        // 1º turno: acumulado 0 → passa; emite 25 chars.
        var first = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        Assert.NotEqual("stop", first);

        // 2º turno: acumulado 25 → passa; emite mais 25 (total 50).
        var second = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        Assert.NotEqual("stop", second);

        // 3º turno: acumulado 50 > 30 → corte por orçamento.
        var third = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        Assert.Equal("stop", third);

        Assert.Equal(TraceOutcome.Budget, Trace.Load()[^1].Outcome);
    }

    [Fact]
    public void Dispatch_SemTetoConfigurado_NaoCortaPorCusto()
    {
        // Default: maxInstructionChars=0 → só o teto de passos governa.
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

        // Novo workflow: reset zera CostChars junto com o Step.
        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks);

        Assert.NotEqual("stop", result);
        // O reset zera o acumulado, restando apenas a instrução emitida pelo próprio start.
        Assert.Equal("PROMPT_START".Length, StateStore.Load().CostChars);
    }
}
