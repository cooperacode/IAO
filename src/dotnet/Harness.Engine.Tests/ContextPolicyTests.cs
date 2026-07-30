using Harness.Engine;

namespace Harness.Engine.Tests;

public class ContextPolicyTests : IDisposable
{
    public ContextPolicyTests() => Clean();
    public void Dispose() => Clean();

    private static void Clean()
    {
        if (File.Exists("harness.json"))
            File.Delete("harness.json");
        Environment.SetEnvironmentVariable("HARNESS_CONTEXT_USAGE_JSON", null);
        StateStore.Reset();
        HarnessConfig.Reload();
    }

    [Fact]
    public void Adaptive_UsesTelemetryAndResetsAtThreshold()
    {
        File.WriteAllText("harness.json", "{\"contextResetMode\":\"adaptive\",\"contextResetThreshold\":0.7,\"contextFallbackFeatures\":1}");
        HarnessConfig.Reload();

        Assert.StartsWith("=== NEW SESSION", ContextPolicy.NewFeaturePrefix());
        ContextPolicy.Observe(new ContextUsage("iao.context.v1", "s1", 100, 50, "driver"));
        Assert.Equal(string.Empty, ContextPolicy.NewFeaturePrefix());
        ContextPolicy.Observe(new ContextUsage("iao.context.v1", "s1", 100, 80, "driver"));
        Assert.StartsWith("=== NEW SESSION", ContextPolicy.NewFeaturePrefix());
    }

    [Fact]
    public void ContextUsage_ReadsCanonicalEnvironmentHook()
    {
        Environment.SetEnvironmentVariable(
            "HARNESS_CONTEXT_USAGE_JSON",
            "{\"contextWindowTokens\":100,\"contextUsedTokens\":70,\"source\":\"host\"}");

        var usage = ContextUsage.FromEnvironment();

        Assert.NotNull(usage);
        Assert.Equal(100, usage.ContextWindowTokens);
        Assert.Equal(70, usage.ContextUsedTokens);
    }
}
