using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// Deterministic evaluators are pure functions — tested without touching disk or an LLM.
/// They're the cheap gate before the LLM judge.
/// </summary>
public class EvaluatorsTests
{
    [Theory]
    [InlineData("Bug", "Bug", 1.0)]
    [InlineData("Bug", "  Bug  ", 1.0)]
    [InlineData("Bug", "Epic", 0.0)]
    public void ExactMatch_NormalizaEspacosEComparaConteudo(string expected, string actual, double value)
    {
        Assert.Equal(value, Evaluators.ExactMatch(expected, actual).Value);
    }

    [Fact]
    public void MatchesRegex_AvaliaOPadrao()
    {
        Assert.True(Evaluators.MatchesRegex(@"^\d+\s*pts$", "13 pts").Passed);
        Assert.False(Evaluators.MatchesRegex(@"^\d+\s*pts$", "treze").Passed);
    }

    [Fact]
    public void Trajectory_CaminhoIdentico_PontuaCheio()
    {
        var expected = new[] { "start", "classify", "finalize" };

        var score = Evaluators.Trajectory(expected, ["start", "classify", "finalize"]);

        Assert.True(score.Passed);
        Assert.Equal(1.0, score.Value);
    }

    [Fact]
    public void Trajectory_DivergeNoMeio_ContaSoOPrefixoEmOrdem()
    {
        var expected = new[] { "start", "classify", "split", "finalize" };

        // Matches start+classify, then jumps straight to finalize (out of order).
        var score = Evaluators.Trajectory(expected, ["start", "classify", "finalize"]);

        Assert.Equal(0.5, score.Value); // 2 of 4
        Assert.False(score.Passed);
    }

    [Fact]
    public void Trajectory_EsperadoVazio_PontuaCheio()
    {
        Assert.True(Evaluators.Trajectory([], []).Passed);
    }

    [Fact]
    public void Completeness_ContaChavesPreenchidas()
    {
        var state = new HarnessState(3, new()
        {
            ["description"] = "Login",
            ["type"] = "Feature",
            ["stories"] = "   ", // blank doesn't count
        });

        var score = Evaluators.Completeness(state, ["description", "type", "stories"]);

        Assert.Equal(2.0 / 3.0, score.Value, precision: 6);
        Assert.False(score.Passed);
    }

    [Fact]
    public void StepBudget_ConcluiuComStop_Passa()
    {
        var trace = new[]
        {
            new TraceEntry(1, "start", TraceOutcome.Instruction, 100, default),
            new TraceEntry(2, "finalize", TraceOutcome.Stop, 4, default),
        };

        Assert.True(Evaluators.StepBudget(trace).Passed);
    }

    [Fact]
    public void StepBudget_CortadoPeloTeto_Falha()
    {
        var trace = new[]
        {
            new TraceEntry(1, "classify", TraceOutcome.Instruction, 100, default),
            new TraceEntry(13, "classify", TraceOutcome.Budget, 4, default),
        };

        Assert.False(Evaluators.StepBudget(trace).Passed);
    }

    [Fact]
    public void StepBudget_CortadoPeloTimeout_FalhaEDistingueDeNaoTerminou()
    {
        var trace = new[]
        {
            new TraceEntry(1, "classify", TraceOutcome.Instruction, 100, default),
            new TraceEntry(2, "slow", TraceOutcome.Timeout, 4, default),
        };

        var score = Evaluators.StepBudget(trace);

        Assert.False(score.Passed);
        Assert.Equal("cut off by the time ceiling (timeout)", score.Detail);
    }

    [Fact]
    public void CommandsOf_IgnoraVoltasDeErroPorPadrao()
    {
        var trace = new[]
        {
            new TraceEntry(1, "start", TraceOutcome.Instruction, 100, default),
            new TraceEntry(2, "(unparsed)", TraceOutcome.Error, 200, default),
            new TraceEntry(3, "classify", TraceOutcome.Instruction, 150, default),
        };

        Assert.Equal(["start", "classify"], Evaluators.CommandsOf(trace));
        Assert.Equal(["start", "(unparsed)", "classify"], Evaluators.CommandsOf(trace, includeErrors: true));
    }
}
