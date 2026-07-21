using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// Evaluators determinísticos são funções puras — testadas sem tocar disco nem LLM.
/// São o portão barato antes do juiz-LLM.
/// </summary>
public class EvaluatorsTests
{
    [Theory]
    [InlineData("Bug", "Bug", 1.0)]
    [InlineData("Bug", "  Bug  ", 1.0)]
    [InlineData("Bug", "Épico", 0.0)]
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
        var esperado = new[] { "start", "classify", "finalize" };

        var score = Evaluators.Trajectory(esperado, ["start", "classify", "finalize"]);

        Assert.True(score.Passed);
        Assert.Equal(1.0, score.Value);
    }

    [Fact]
    public void Trajectory_DivergeNoMeio_ContaSoOPrefixoEmOrdem()
    {
        var esperado = new[] { "start", "classify", "split", "finalize" };

        // Acerta start+classify, depois pula direto para finalize (fora de ordem).
        var score = Evaluators.Trajectory(esperado, ["start", "classify", "finalize"]);

        Assert.Equal(0.5, score.Value); // 2 de 4
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
            ["descricao"] = "Login",
            ["tipo"] = "Feature",
            ["historias"] = "   ", // em branco não conta
        });

        var score = Evaluators.Completeness(state, ["descricao", "tipo", "historias"]);

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
