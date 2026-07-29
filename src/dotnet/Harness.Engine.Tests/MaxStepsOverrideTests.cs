using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// The per-invocation <c>maxSteps</c> override: a long-running flow (e.g. Development)
/// raises the global ceiling only for its own process, without touching the shared
/// <c>harness.json</c> (Refinement keeps its 12 steps). With no override, the config's
/// ceiling applies.
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

        Assert.Equal("stop", last); // step MaxSteps+1 is cut off by the global guard
    }

    [Fact]
    public void ComOverrideMaior_NaoCortaAlemDoTetoGlobal()
    {
        var last = "";
        for (var i = 0; i < TaskRegistry.MaxSteps + 5; i++)
            last = Ping(TaskRegistry.MaxSteps + 20);

        Assert.NotEqual("stop", last); // the override gave the slack the global ceiling wouldn't
    }
}
