using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Engine;

/// <summary>
/// Data contract exchanged between the driver (agent) and the state machine.
/// The model returns this envelope as JSON; the engine dispatches by <see cref="Value"/>.
///
/// There is no token field: the typical driver is an LLM with no access to its own
/// request's <c>usage</c>, so any self-reported count would be confabulated. The cost
/// ceiling only uses measures the engine can attest on its own (steps and instruction
/// chars — see <see cref="TaskRegistry"/>); real tokens live in the caller's billing
/// metadata.
/// </summary>
public record Envelope(
    string Type,
    string Value,
    string[]? Args)
{
    // `init` property (non-positional) so it doesn't break the `new Envelope(Type, Value,
    // Args)` calls already spread across the flows — same reason as HarnessState.CostChars.
    // Born in the `start` envelope (see TaskRegistry) and reinjected into every output by
    // PromptFormatter, without every task needing to pass it along.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Context { get; init; }

    public string ToJson() => Serialize(this);

    /// <summary>Tolerant parse: accepts markdown fences and surrounding text around the JSON object.</summary>
    public static Envelope? Parse(string value) => TryParse(value);

    // `record` promises value semantics, but arrays are compared by reference — without
    // this, two envelopes with identical content would not be equal.
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

        // Order-independent: Equals ignores the pairs' order, so the hash needs to combine
        // without depending on it (otherwise two "equal" envelopes would have different hashes).
        var contextHash = 0;
        foreach (var kv in Context ?? [])
            contextHash ^= HashCode.Combine(kv.Key, kv.Value);
        hash.Add(contextHash);

        return hash.ToHashCode();
    }

    // Source-generated: anonymous type + reflection don't survive Native AOT.
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
            // Diagnostics go to stderr — stdout is the harness's transport channel (the
            // driver reads stdout as the next instruction) and must not be polluted.
            Console.Error.WriteLine(ex);
            return null;
        }
    }

    // Models frequently wrap the JSON in markdown fences (```json … ```) or add
    // surrounding text. Normalizes to the raw JSON object before parsing.
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
