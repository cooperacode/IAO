using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// O lote é o Task Registry (#2) como registry de avaliação: agrega os evaluators
/// determinísticos sobre um golden set. Puro — testado sem disco nem LLM.
/// </summary>
public class BatchEvaluatorTests
{
    private static readonly string[] HappyPath =
        ["start", "classify", "split", "acceptance", "estimate", "risks", "ready_check", "finalize"];

    private static readonly string[] Keys = ["descricao", "tipo", "veredito"];

    private static IReadOnlyList<TraceEntry> TraceOf(IEnumerable<string> commands)
    {
        var list = commands.ToList();
        return list.Select((cmd, i) => new TraceEntry(
            i + 1,
            cmd,
            i == list.Count - 1 ? TraceOutcome.Stop : TraceOutcome.Instruction,
            100,
            default)).ToList();
    }

    private static HarnessState StateWith(params string[] filledKeys) =>
        new(filledKeys.Length, filledKeys.ToDictionary(k => k, _ => "x"));

    [Fact]
    public void Evaluate_RunPerfeito_PassaTodasAsMetricas()
    {
        var golden = new GoldenCase("ok", "caso bom", HappyPath, Keys);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(HappyPath), StateWith(Keys));

        Assert.True(result.Passed);
        Assert.Contains(result.Scores, s => s.Metric == "trajectory" && s.Passed);
        Assert.Contains(result.Scores, s => s.Metric == "completeness" && s.Passed);
        Assert.Contains(result.Scores, s => s.Metric == "budget" && s.Passed);
    }

    [Fact]
    public void Evaluate_TrajetoriaIncompleta_Reprova()
    {
        var golden = new GoldenCase("ruim", "pulou passos", HappyPath, Keys);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(["start", "classify", "finalize"]), StateWith(Keys));

        Assert.False(result.Passed);
        Assert.Contains(result.Scores, s => s.Metric == "trajectory" && !s.Passed);
    }

    [Fact]
    public void Evaluate_EstadoIncompleto_Reprova()
    {
        var golden = new GoldenCase("faltou", "sem veredito", HappyPath, Keys);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(HappyPath), StateWith("descricao", "tipo"));

        Assert.False(result.Passed);
        Assert.Contains(result.Scores, s => s.Metric == "completeness" && !s.Passed);
    }

    [Fact]
    public void EvaluateAll_AgregaTaxaDeAcerto()
    {
        var bom = new GoldenCase("bom", "", HappyPath, Keys);
        var ruim = new GoldenCase("ruim", "", HappyPath, Keys);

        var batch = BatchEvaluator.EvaluateAll(
        [
            (bom, TraceOf(HappyPath), StateWith(Keys)),
            (ruim, TraceOf(["start", "classify"]), StateWith(Keys)),
        ]);

        Assert.Equal(2, batch.Total);
        Assert.Equal(1, batch.PassedCount);
        Assert.Equal(0.5, batch.PassRate);
    }

    [Fact]
    public void EvaluateAll_LoteVazio_PassRateZero()
    {
        Assert.Equal(0.0, BatchEvaluator.EvaluateAll([]).PassRate);
    }

    [Fact]
    public void Evaluate_CasoNegativoIntencional_QueReprovaNasMetricas_ContaComoOk()
    {
        var golden = new GoldenCase("negativo", "trajetória ok, conteúdo faltando", HappyPath, Keys, ExpectPass: false);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(HappyPath), StateWith("descricao", "tipo")); // faltou veredito

        Assert.False(result.Passed); // reprova nas métricas...
        Assert.True(result.Ok);      // ...que é exatamente o comportamento esperado
    }

    [Fact]
    public void Evaluate_CasoNegativoQueDeixaDeReprovar_ContaComoFalha()
    {
        var golden = new GoldenCase("negativo", "deveria reprovar", HappyPath, Keys, ExpectPass: false);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(HappyPath), StateWith(Keys)); // agora passa em tudo

        Assert.True(result.Passed);
        Assert.False(result.Ok); // esperava-se reprovação e não houve → o caso deixou de exercer o que deveria
    }

    [Fact]
    public void EvaluateAll_CasoNegativoQueReprovaMantemASuiteVerde()
    {
        var bom = new GoldenCase("bom", "", HappyPath, Keys);
        var neg = new GoldenCase("neg", "", HappyPath, Keys, ExpectPass: false);

        var batch = BatchEvaluator.EvaluateAll(
        [
            (bom, TraceOf(HappyPath), StateWith(Keys)),
            (neg, TraceOf(HappyPath), StateWith("descricao", "tipo")),
        ]);

        Assert.Equal(2, batch.PassedCount); // ambos se comportaram como esperado
        Assert.Equal(1.0, batch.PassRate);
    }
}
