using System.Globalization;

namespace Harness.Engine;

/// <summary>
/// Driver-agnostic policy for deciding when the next feature should request a clean
/// driver context. Missing or invalid telemetry falls back to a deterministic feature
/// boundary; the engine never reads a driver's private rollout storage.
/// </summary>
public static class ContextPolicy
{
    private const string BoundarySeenKey = "context_boundary_seen";
    private const string FeaturesKey = "context_features";
    private const string RatioKey = "context_ratio";
    private const string UsageSeenKey = "context_usage_seen";

    public static void Observe(ContextUsage? usage)
    {
        if (usage is null || usage.ContextWindowTokens <= 0 || usage.ContextUsedTokens < 0)
            return;

        var ratio = Math.Clamp(
            (double)usage.ContextUsedTokens / usage.ContextWindowTokens,
            0d,
            1d);
        StateStore.Set(RatioKey, ratio.ToString("R", CultureInfo.InvariantCulture));
        StateStore.Set(UsageSeenKey, "true");
    }

    /// <summary>Returns the optional marker prefix for a newly selected feature.</summary>
    public static string NewFeaturePrefix()
    {
        var fresh = ShouldReset();
        StateStore.Set(BoundarySeenKey, "true");

        if (fresh)
        {
            StateStore.Set(FeaturesKey, "1");
            StateStore.Set(RatioKey, "0");
            StateStore.Set(UsageSeenKey, "false");
            return "=== NEW SESSION (clean context) ===\n\n";
        }

        var features = ReadInt(FeaturesKey) + 1;
        StateStore.Set(FeaturesKey, features.ToString(CultureInfo.InvariantCulture));
        return string.Empty;
    }

    private static bool ShouldReset()
    {
        var config = HarnessConfig.Current;
        var mode = config.ContextResetMode.Trim().ToLowerInvariant();
        if (mode == "never")
            return false;
        if (mode == "per-feature")
            return true;
        if (StateStore.Get(BoundarySeenKey) is null)
            return true;

        if (double.TryParse(
                StateStore.Get(RatioKey),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var ratio)
            && ratio >= config.ContextResetThreshold)
            return true;

        return StateStore.Get(UsageSeenKey) != "true"
            && config.ContextFallbackFeatures > 0
            && ReadInt(FeaturesKey) >= config.ContextFallbackFeatures;
    }

    private static int ReadInt(string key) =>
        int.TryParse(StateStore.Get(key), out var value) ? value : 0;
}
