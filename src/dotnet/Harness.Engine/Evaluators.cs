using System.Text.RegularExpressions;

namespace Harness.Engine;

/// <summary>
/// Evaluators determinísticos (Exact Match, Regex, Trajectory) — o Evaluator #6 do
/// diagrama na parte que NÃO precisa de LLM. Rodam in-process sobre o <see cref="Trace"/>
/// e o <see cref="HarnessState"/>, custam zero token e servem de portão: só quando
/// passam vale a pena escalar para o juiz-LLM (economia sob a restrição de tokens).
/// </summary>
public static class Evaluators
{
    public static Score ExactMatch(string expected, string actual) =>
        new("exact_match", Norm(expected) == Norm(actual) ? 1.0 : 0.0,
            $"esperado=\"{expected}\" obtido=\"{actual}\"");

    public static Score MatchesRegex(string pattern, string actual) =>
        new("regex", Regex.IsMatch(actual, pattern) ? 1.0 : 0.0, pattern);

    /// <summary>
    /// Fração do prefixo esperado que bateu, na ordem. Um passo fora de sequência corta
    /// a contagem ali — trajetória é sobre caminho, não sobre conjunto.
    /// </summary>
    public static Score Trajectory(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var matched = 0;
        for (var i = 0; i < expected.Count && i < actual.Count; i++)
        {
            if (expected[i] != actual[i])
                break;
            matched++;
        }

        var value = expected.Count == 0 ? 1.0 : (double)matched / expected.Count;
        return new("trajectory", value, $"{matched}/{expected.Count} passos na ordem esperada");
    }

    /// <summary>Todas as chaves de domínio esperadas foram preenchidas no estado final?</summary>
    public static Score Completeness(HarnessState state, IReadOnlyList<string> requiredKeys)
    {
        var filled = requiredKeys.Count(k => !string.IsNullOrWhiteSpace(state.Data.GetValueOrDefault(k, "")));
        var value = requiredKeys.Count == 0 ? 1.0 : (double)filled / requiredKeys.Count;
        return new("completeness", value, $"{filled}/{requiredKeys.Count} chaves preenchidas");
    }

    /// <summary>Terminou em <see cref="TraceOutcome.Stop"/> sem ter batido no teto de passos.</summary>
    public static Score StepBudget(IReadOnlyList<TraceEntry> trace)
    {
        var hitBudget = trace.Any(e => e.Outcome == TraceOutcome.Budget);
        var terminated = trace.Any(e => e.Outcome == TraceOutcome.Stop);

        return new("budget", !hitBudget && terminated ? 1.0 : 0.0,
            hitBudget ? "cortado pelo teto de passos"
            : terminated ? "concluído dentro do teto"
            : "não terminou");
    }

    /// <summary>Comandos do trace na ordem, ignorando por padrão as voltas de erro corretivo.</summary>
    public static IReadOnlyList<string> CommandsOf(IReadOnlyList<TraceEntry> trace, bool includeErrors = false) =>
        trace.Where(e => includeErrors || e.Outcome != TraceOutcome.Error)
             .Select(e => e.Command)
             .ToList();

    private static string Norm(string value) => value.Trim();
}

/// <summary>Nota de uma métrica em [0,1]. <see cref="Passed"/> exige acerto pleno.</summary>
public record Score(string Metric, double Value, string Detail = "")
{
    public bool Passed => Value >= 1.0;
}
