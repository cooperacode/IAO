using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// O override de <c>maxSteps</c> por invocação: um flow long-running (ex.: Development) levanta
/// o teto global só para o seu processo, sem tocar o <c>harness.json</c> compartilhado (o
/// Refinement segue com os 12 passos). Sem override, vale o teto do config.
/// </summary>
public class MaxStepsOverrideTests : IDisposable
{
    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["ping"] = _ => "PONG",
    };

    public MaxStepsOverrideTests() => Clean();
    public void Dispose() => Clean();

    private static void Clean()
    {
        StateStore.Reset();
        Trace.Reset();
    }

    private static string Ping(int? maxSteps) =>
        TaskRegistry.Dispatch(["""{"type":"tool","value":"ping"}"""], Tasks, null, maxSteps);

    [Fact]
    public void SemOverride_CortaNoTetoGlobal()
    {
        var last = "";
        for (var i = 0; i < TaskRegistry.MaxSteps + 1; i++)
            last = Ping(null);

        Assert.Equal("stop", last); // o passo MaxSteps+1 é cortado pela guarda global
    }

    [Fact]
    public void ComOverrideMaior_NaoCortaAlemDoTetoGlobal()
    {
        var last = "";
        for (var i = 0; i < TaskRegistry.MaxSteps + 5; i++)
            last = Ping(TaskRegistry.MaxSteps + 20);

        Assert.NotEqual("stop", last); // o override deu a folga que o global não daria
    }
}
