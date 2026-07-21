using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// O <see cref="HarnessHost"/> congela a evidência (trajetória + estado) ao concluir um
/// flow. A regressão que importa: a avaliação — que também termina em <c>stop</c> — NÃO
/// pode sobrescrever a evidência do refinamento, senão a reavaliação lê o trace errado.
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
        StateStore.Set("descricao", "x");

        HarnessHost.Run(["""{"type":"command","value":"finalize"}"""], FinalizeTask);

        Assert.True(File.Exists(Trace.LastRunPath));
        Assert.True(File.Exists(StateStore.LastRunStatePath));
        Assert.Equal("x", StateStore.LoadFrom(StateStore.LastRunStatePath).Data.GetValueOrDefault("descricao"));
    }

    [Fact]
    public void Run_Avaliacao_NaoSobrescreveAEvidenciaDoRefinamento()
    {
        // 1) Refinamento conclui → last-run.* guarda a evidência do refinamento.
        StateStore.Set("descricao", "refino");
        HarnessHost.Run(["""{"type":"command","value":"finalize"}"""], FinalizeTask);
        var refinoTrace = File.ReadAllText(Trace.LastRunPath);

        // 2) Avaliação conclui usando os SEUS caminhos (last-evaluation.*).
        HarnessHost.Run(
            ["""{"type":"text","value":"start"}"""],
            new Dictionary<string, Func<Envelope?, string>> { ["start"] = _ => "stop" },
            Trace.LastEvaluationPath,
            StateStore.LastEvaluationStatePath);

        // A avaliação gravou a própria evidência...
        Assert.True(File.Exists(Trace.LastEvaluationPath));
        // ...e NÃO tocou na do refinamento.
        Assert.Equal(refinoTrace, File.ReadAllText(Trace.LastRunPath));
        Assert.Equal("refino", StateStore.LoadFrom(StateStore.LastRunStatePath).Data.GetValueOrDefault("descricao"));
    }
}
