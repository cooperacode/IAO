using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// <see cref="HarnessHost"/> freezes the evidence (trajectory + state) when a flow
/// completes. The regression that matters: evaluation — which also ends in <c>stop</c> —
/// must NOT overwrite refinement's evidence, or a re-evaluation reads the wrong trace.
/// </summary>
public class HarnessHostTests : IDisposable
{
    private static readonly Dictionary<string, Func<Envelope?, string>> FinalizeTask =
        new() { ["finalize"] = _ => "stop" };

    public HarnessHostTests() => Clean();
    public void Dispose() => Clean();

    private static void Clean()
    {
        StateStore.Reset();
        Trace.Reset();
        foreach (var p in new[]
        {
            Trace.LastRunPath, Trace.LastEvaluationPath,
            StateStore.LastRunStatePath, StateStore.LastEvaluationStatePath,
        })
        {
            if (File.Exists(p))
                File.Delete(p);
        }
    }

    [Fact]
    public void Run_AoConcluir_CongelaTrajetoriaEEstadoNoCaminhoDoFlow()
    {
        StateStore.Set("description", "x");

        HarnessHost.Run(["""{"type":"command","value":"finalize"}"""], FinalizeTask);

        Assert.True(File.Exists(Trace.LastRunPath));
        Assert.True(File.Exists(StateStore.LastRunStatePath));
        Assert.Equal("x", StateStore.LoadFrom(StateStore.LastRunStatePath).Data.GetValueOrDefault("description"));
    }

    [Fact]
    public void Run_Avaliacao_NaoSobrescreveAEvidenciaDoRefinamento()
    {
        // 1) Refinement completes → last-run.* keeps refinement's evidence.
        StateStore.Set("description", "refinement");
        HarnessHost.Run(["""{"type":"command","value":"finalize"}"""], FinalizeTask);
        var refinementTrace = File.ReadAllText(Trace.LastRunPath);

        // 2) Evaluation completes using ITS OWN paths (last-evaluation.*).
        HarnessHost.Run(
            ["""{"type":"text","value":"start"}"""],
            new Dictionary<string, Func<Envelope?, string>> { ["start"] = _ => "stop" },
            Trace.LastEvaluationPath,
            StateStore.LastEvaluationStatePath);

        // Evaluation wrote its own evidence...
        Assert.True(File.Exists(Trace.LastEvaluationPath));
        // ...and did NOT touch refinement's.
        Assert.Equal(refinementTrace, File.ReadAllText(Trace.LastRunPath));
        Assert.Equal("refinement", StateStore.LoadFrom(StateStore.LastRunStatePath).Data.GetValueOrDefault("description"));
    }
}
