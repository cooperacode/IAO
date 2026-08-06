using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Fixed harness variables, externalized into a <c>harness.json</c> at the repo root.
/// Previously these were hardcoded constants scattered around (<see cref="TaskRegistry"/>,
/// <see cref="DocsReader"/>, <c>RefinementTasks</c>); centralizing them here lets each
/// flow/environment adjust the ceilings without recompiling. Missing or unreadable → falls
/// back to <see cref="Default"/> (same tolerance as <see cref="StateStore.LoadFrom"/>:
/// config is optional input, it can't bring down the run).
///
/// Top-level type (not nested) so it's servable by System.Text.Json's source generator, a
/// Native AOT requirement.
/// </summary>
public record HarnessConfig(
    int MaxSteps,
    int MaxInstructionChars,
    int DocsMaxChars,
    string DocsFolder,
    int TimeoutMs,
    string ContextResetMode,
    double ContextResetThreshold,
    int ContextFallbackFeatures)
{
    // Step ceiling: prevents an infinite loop that would burn tokens indefinitely.
    // MaxInstructionChars = 0 disables the cost ceiling (only the step one applies).
    // TimeoutMs is always enabled: a workspace config may tune it but cannot turn off the
    // per-step time guard.
    public static HarnessConfig Default { get; } = new(
        MaxSteps: 12,
        MaxInstructionChars: 0,
        DocsMaxChars: 40_000,
        DocsFolder: "specs",
        TimeoutMs: 10 * 60_000,
        ContextResetMode: "adaptive",
        ContextResetThreshold: 0.70,
        ContextFallbackFeatures: 1);

    private const string FilePath = "harness.json";

    // Hard ceiling on timeoutMs, regardless of the source (harness.json OR the env var
    // below). harness.json lives in the working directory the supervised agent itself
    // controls: without this ceiling, the agent could edit the file to grant itself an
    // arbitrarily high timeout and never get cut off by the time guard (see TaskRegistry).
    private const int MaxAllowedTimeoutMs = 10 * 60_000;
    private const int MinEnabledTimeoutMs = 1;

    // When set, overrides harness.json's timeoutMs. Unlike the file, the env var is set by
    // the parent process that invokes each harness step — outside the working directory
    // the supervised agent controls — so it can't be self-edited by the same agent the
    // timeout is meant to contain.
    private const string TimeoutMsEnvVar = "HARNESS_TIMEOUT_MS";

    // Loaded once per process (each harness invocation is a new process, so "once" = "per
    // loop turn"). Static readers — DocsReader, RefinementTasks — consume from here without
    // needing to receive the config as a parameter.
    private static HarnessConfig? _current;
    public static HarnessConfig Current => _current ??= Load();

    /// <summary>Forces a re-read of <c>harness.json</c> — for tests and long-lived drivers.</summary>
    public static void Reload() => _current = Load();

    /// <summary>Re-reads <c>harness.json</c> from disk; any failure returns <see cref="Default"/>.</summary>
    public static HarnessConfig Load()
    {
        var config = Default;
        try
        {
            var path = PathResolver.Resolve(FilePath);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.HarnessConfig);
                if (parsed is not null)
                    config = parsed;
            }
        }
        catch (Exception ex)
        {
            HarnessLog.Error($"[HarnessConfig] failed to load; using defaults: {ex.Message}");
            config = Default;
        }

        return Normalize(ApplyTimeoutEnvOverride(config));
    }

    // See TimeoutMsEnvVar. Missing/invalid is silently ignored — same tolerance as the
    // rest of the config: it's optional input, it can't bring down the run.
    private static HarnessConfig ApplyTimeoutEnvOverride(HarnessConfig config)
    {
        var raw = Environment.GetEnvironmentVariable(TimeoutMsEnvVar);
        return int.TryParse(raw, out var timeoutMs) ? config with { TimeoutMs = timeoutMs } : config;
    }

    // A partial harness.json deserializes missing fields as 0/null. Zero is only valid
    // where it means "disabled" (cost ceilings); elsewhere, a missing field = default.
    private static HarnessConfig Normalize(HarnessConfig config) => config with
    {
        MaxSteps = config.MaxSteps > 0 ? config.MaxSteps : Default.MaxSteps,
        MaxInstructionChars = int.Max(config.MaxInstructionChars, 0),
        DocsMaxChars = config.DocsMaxChars > 0 ? config.DocsMaxChars : Default.DocsMaxChars,
        DocsFolder = string.IsNullOrWhiteSpace(config.DocsFolder) ? Default.DocsFolder : config.DocsFolder,
        // A workspace-controlled config may tune the timeout, but it may not turn the
        // guard off. The terminal latch in TaskRegistry is the second stop mechanism for
        // a timeout that already occurred.
        TimeoutMs = int.Clamp(
            config.TimeoutMs > 0 ? config.TimeoutMs : Default.TimeoutMs,
            MinEnabledTimeoutMs,
            MaxAllowedTimeoutMs),
        ContextResetMode = config.ContextResetMode is "adaptive" or "per-feature" or "never"
            ? config.ContextResetMode
            : Default.ContextResetMode,
        ContextResetThreshold = double.Clamp(
            config.ContextResetThreshold is > 0 and <= 1 ? config.ContextResetThreshold : Default.ContextResetThreshold,
            0.1,
            1.0),
        ContextFallbackFeatures = config.ContextFallbackFeatures > 0
            ? config.ContextFallbackFeatures
            : Default.ContextFallbackFeatures,
    };
}
