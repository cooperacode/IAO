using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// The batch is Task Registry (#2) as an evaluation registry: aggregates the
/// deterministic evaluators over a golden set. Pure — tested without disk or an LLM.
/// </summary>
public class BatchEvaluatorTests
{
    private static readonly string[] HappyPath =
        ["start", "classify", "split", "acceptance", "estimate", "risks", "ready_check", "finalize"];

    private static readonly string[] Keys = ["description", "type", "verdict"];

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
        var golden = new GoldenCase("ok", "good case", HappyPath, Keys);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(HappyPath), StateWith(Keys));

        Assert.True(result.Passed);
        Assert.Contains(result.Scores, s => s.Metric == "trajectory" && s.Passed);
        Assert.Contains(result.Scores, s => s.Metric == "completeness" && s.Passed);
        Assert.Contains(result.Scores, s => s.Metric == "budget" && s.Passed);
    }

    [Fact]
    public void Evaluate_TrajetoriaIncompleta_Reprova()
    {
        var golden = new GoldenCase("ruim", "skipped steps", HappyPath, Keys);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(["start", "classify", "finalize"]), StateWith(Keys));

        Assert.False(result.Passed);
        Assert.Contains(result.Scores, s => s.Metric == "trajectory" && !s.Passed);
    }

    [Fact]
    public void Evaluate_EstadoIncompleto_Reprova()
    {
        var golden = new GoldenCase("faltou", "no verdict", HappyPath, Keys);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(HappyPath), StateWith("description", "type"));

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
        var golden = new GoldenCase("negativo", "trajectory ok, missing content", HappyPath, Keys, ExpectPass: false);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(HappyPath), StateWith("description", "type")); // missing verdict

        Assert.False(result.Passed); // fails the metrics...
        Assert.True(result.Ok);      // ...which is exactly the expected behavior
    }

    [Fact]
    public void Evaluate_CasoNegativoQueDeixaDeReprovar_ContaComoFalha()
    {
        var golden = new GoldenCase("negativo", "should fail", HappyPath, Keys, ExpectPass: false);

        var result = BatchEvaluator.Evaluate(golden, TraceOf(HappyPath), StateWith(Keys)); // now passes everything

        Assert.True(result.Passed);
        Assert.False(result.Ok); // a failure was expected and didn't happen → the case stopped exercising what it should
    }

    [Fact]
    public void EvaluateAll_CasoNegativoQueReprovaMantemASuiteVerde()
    {
        var good = new GoldenCase("bom", "", HappyPath, Keys);
        var neg = new GoldenCase("neg", "", HappyPath, Keys, ExpectPass: false);

        var batch = BatchEvaluator.EvaluateAll(
        [
            (good, TraceOf(HappyPath), StateWith(Keys)),
            (neg, TraceOf(HappyPath), StateWith("description", "type")),
        ]);

        Assert.Equal(2, batch.PassedCount); // both behaved as expected
        Assert.Equal(1.0, batch.PassRate);
    }
}
