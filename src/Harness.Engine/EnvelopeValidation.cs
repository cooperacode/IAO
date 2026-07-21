using System.Text.RegularExpressions;

namespace Harness.Engine;

/// <summary>Resultado de uma validação contextual: ok, ou a razão da recusa (para o erro corretivo).</summary>
public readonly record struct ValidationResult(bool Ok, string Reason)
{
    public static ValidationResult Pass { get; } = new(true, string.Empty);
    public static ValidationResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Predicados determinísticos e baratos para validar se o valor devolvido pelo driver
/// atende à expectativa da task — ANTES de persisti-lo e seguir o flow. Falhou → o
/// <see cref="TaskRegistry"/> devolve um erro corretivo tipado e o driver reenvia
/// (loop corretivo, não término mudo).
///
/// Validação semântica profunda continua sendo trabalho do juiz-LLM na avaliação;
/// aqui mora só o que é checável em código, com zero token.
/// </summary>
public static class EnvelopeValidation
{
    /// <summary>O primeiro arg existe e não é vazio/whitespace.</summary>
    public static Func<Envelope, ValidationResult> NotEmpty(string expectation) =>
        envelope => FirstArg(envelope) is { Length: > 0 }
            ? ValidationResult.Pass
            : ValidationResult.Fail($"O argumento esperado veio vazio. Esperado: {expectation}.");

    /// <summary>O primeiro arg tem ao menos <paramref name="count"/> linhas não vazias (contando <c>\n</c> literais).</summary>
    public static Func<Envelope, ValidationResult> MinLines(int count, string expectation) =>
        envelope =>
        {
            var lines = Lines(FirstArg(envelope));
            return lines >= count
                ? ValidationResult.Pass
                : ValidationResult.Fail(
                    $"O argumento tem {lines} linha(s) úteis, mas a task espera ao menos {count}. Esperado: {expectation}.");
        };

    /// <summary>O primeiro arg contém ao menos um número.</summary>
    public static Func<Envelope, ValidationResult> ContainsNumber(string expectation) =>
        envelope => Regex.IsMatch(FirstArg(envelope), @"\d")
            ? ValidationResult.Pass
            : ValidationResult.Fail($"O argumento não contém nenhum número. Esperado: {expectation}.");

    /// <summary>O primeiro arg casa com o padrão (case-insensitive).</summary>
    public static Func<Envelope, ValidationResult> Matches(string pattern, string expectation) =>
        envelope => Regex.IsMatch(FirstArg(envelope), pattern, RegexOptions.IgnoreCase)
            ? ValidationResult.Pass
            : ValidationResult.Fail($"O argumento não atende ao formato esperado. Esperado: {expectation}.");

    /// <summary>Composição: todos os predicados precisam passar; o primeiro que falhar dá a razão.</summary>
    public static Func<Envelope, ValidationResult> All(params Func<Envelope, ValidationResult>[] validators) =>
        envelope =>
        {
            foreach (var validator in validators)
            {
                var result = validator(envelope);
                if (!result.Ok)
                    return result;
            }

            return ValidationResult.Pass;
        };

    private static string FirstArg(Envelope envelope) =>
        envelope.Args is { Length: > 0 } args ? args[0].Trim() : string.Empty;

    // Artefatos trafegam como string JSON de uma linha com \n literais (ver o aviso
    // "Compact" dos flows) — conta tanto quebras reais quanto escapadas.
    private static int Lines(string value) =>
        value.Split(["\n", "\\n"], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => !string.IsNullOrWhiteSpace(line));
}
