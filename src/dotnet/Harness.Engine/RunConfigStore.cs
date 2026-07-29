using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Persists <c>verify_cmd</c>/<c>target_dir</c> (captured once by <c>plan</c>) to
/// <c>.harness/run_config.json</c> — kept out of <c>state.json</c> on purpose.
/// <see cref="TaskRegistry"/> unconditionally resets <c>state.json</c> on every <c>start</c>,
/// before any domain code runs; a resumed run (see
/// <c>Flows.Development.DevelopmentTasks.Start</c>) still needs these two values for
/// <c>smoke</c>/<c>verify</c> to work, so they have to survive that reset.
/// </summary>
public static class RunConfigStore
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/run_config.json";

    /// <summary>Writes the run config — same lifecycle as feature_list.json (written
    /// by <c>plan</c>, erased only when <c>start</c> decides there's no run to resume).</summary>
    public static void Write(RunConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            AtomicIO.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(config, HarnessJsonContext.Default.RunConfig));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RunConfigStore] failed to write: {ex.Message}");
        }
    }

    /// <summary>Reads the persisted config, or the defaults if nothing has been written yet.</summary>
    public static RunConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var config = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.RunConfig);
                if (config is not null)
                    return config;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RunConfigStore] failed to load: {ex.Message}");
        }

        return new RunConfig();
    }

    /// <summary>Erases on a genuinely new run — paired with FeatureStore.Reset().</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RunConfigStore] failed to clear: {ex.Message}");
        }
    }
}

/// <summary>
/// Verify command, target directory, and run identity (RFC §6.4), all captured once by
/// <c>plan</c>. <see cref="RunId"/> is generated only on a genuinely new run — the same
/// moment <see cref="RunConfigStore.Write"/> is called after
/// <see cref="RunConfigStore.Reset"/> — and survives every resume because this file isn't
/// touched when <c>start</c> decides there's pending work (see the class comment). Third
/// positional parameter defaults to <c>""</c> so it doesn't break the
/// <c>new RunConfig(verifyCmd, targetDir)</c> calls already spread across the tests.
/// </summary>
public record RunConfig(string VerifyCmd = "", string TargetDir = ".", string RunId = "");
