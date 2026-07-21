using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Engine;

/// <summary>
/// Contrato de dados trafegado entre o driver (agente) e a máquina de estados.
/// O modelo devolve este envelope como JSON; a engine faz o dispatch por <see cref="Value"/>.
///
/// Não há campo de tokens: o driver típico é um LLM sem acesso ao <c>usage</c> da própria
/// requisição, então qualquer contagem auto-reportada seria confabulada. O teto de custo
/// usa apenas medidas que a engine atesta sozinha (passos e chars de instrução — ver
/// <see cref="TaskRegistry"/>); tokens reais vivem nos metadados de billing do caller.
/// </summary>
public record Envelope(
    string Type,
    string Value,
    string[]? Args)
{
    // Propriedade `init` (não posicional) para não quebrar os `new Envelope(Type, Value,
    // Args)` já espalhados pelos flows — mesmo motivo do HarnessState.CostChars. Nasce no
    // envelope `start` (ver TaskRegistry) e é reinjetada em toda saída por PromptFormatter,
    // sem que cada task precise repassá-la.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Context { get; init; }

    public string ToJson() => Serialize(this);

    /// <summary>Parse tolerante: aceita cercas markdown e texto ao redor do objeto JSON.</summary>
    public static Envelope? Parse(string value) => TryParse(value);

    // `record` promete semântica de valor, mas arrays são comparados por referência —
    // sem isto, dois envelopes de conteúdo idêntico não seriam iguais.
    public virtual bool Equals(Envelope? other) =>
        other is not null
        && Type == other.Type
        && Value == other.Value
        && (Args is null ? other.Args is null : other.Args is not null && Args.SequenceEqual(other.Args))
        && ContextEquals(other);

    private bool ContextEquals(Envelope other) =>
        Context is null
            ? other.Context is null
            : other.Context is not null
                && Context.Count == other.Context.Count
                && Context.All(kv => other.Context.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.Add(Value);

        foreach (var arg in Args ?? [])
            hash.Add(arg);

        // Order-independent: Equals ignora a ordem dos pares, então o hash precisa
        // combinar sem depender dela (senão dois envelopes "iguais" teriam hashes diferentes).
        var contextHash = 0;
        foreach (var kv in Context ?? [])
            contextHash ^= HashCode.Combine(kv.Key, kv.Value);
        hash.Add(contextHash);

        return hash.ToHashCode();
    }

    // Source-generated: tipo anônimo + reflexão não sobrevivem ao Native AOT.
    private static string Serialize(Envelope envelope) =>
        JsonSerializer.Serialize(envelope, HarnessJsonContext.Default.Envelope);

    private static Envelope? TryParse(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("The envelope JSON cannot be null or empty.", nameof(value));

            using var document = JsonDocument.Parse(Sanitize(value));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("The envelope payload must be a JSON object.");

            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            var envelopeValue = root.TryGetProperty("value", out var valueElement)
                ? valueElement.GetString() ?? string.Empty
                : string.Empty;

            var args = root.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
                ? argsElement.EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray()
                : Array.Empty<string>();

            Dictionary<string, string>? context = null;
            if (root.TryGetProperty("context", out var contextElement) && contextElement.ValueKind == JsonValueKind.Object)
            {
                context = new Dictionary<string, string>();
                foreach (var property in contextElement.EnumerateObject())
                    context[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            return new Envelope(type, envelopeValue, args) { Context = context };
        }
        catch (Exception ex)
        {
            // Diagnóstico vai para stderr — stdout é o canal de transporte do harness
            // (o driver lê stdout como a próxima instrução) e não pode ser poluído.
            Console.Error.WriteLine(ex);
            return null;
        }
    }

    // Modelos frequentemente embrulham o JSON em cercas markdown (```json … ```)
    // ou adicionam texto ao redor. Normaliza para o objeto JSON bruto antes do parse.
    private static string Sanitize(string value)
    {
        var v = value.Trim();

        if (v.StartsWith("```"))
        {
            var firstNewLine = v.IndexOf('\n');
            if (firstNewLine >= 0)
                v = v[(firstNewLine + 1)..];

            var closingFence = v.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
                v = v[..closingFence];

            v = v.Trim();
        }

        var start = v.IndexOf('{');
        var end = v.LastIndexOf('}');
        if (start >= 0 && end > start)
            v = v.Substring(start, end - start + 1);

        return v;
    }
}

/// <summary>Sinais de protocolo carregados em <see cref="Envelope.Type"/>.</summary>
public static class EnvelopeType
{
    public const string Text = "text";
    public const string Tool = "tool";
    public const string Command = "command";
    public const string Error = "error";
}
