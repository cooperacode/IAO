using System.Text.Json;

namespace Harness.Engine;

/// <summary>Optional, driver-provided context pressure telemetry.</summary>
public sealed record ContextUsage(
    string Schema,
    string SessionId,
    int ContextWindowTokens,
    int ContextUsedTokens,
    string Source)
{
    public static ContextUsage? FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("HARNESS_CONTEXT_USAGE_JSON");
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JsonSerializer.Deserialize(raw, HarnessJsonContext.Default.ContextUsage);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
