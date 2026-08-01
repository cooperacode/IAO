using System.Text.Json;

namespace Harness.Engine;

/// <summary>
/// Every harness invocation is a new, memory-less process. This store persists the
/// accumulated state (step counter + domain data) to a file, so the envelope carried by
/// the model stays minimal — token savings: the model passes a key, not the whole state,
/// on every loop turn.
/// </summary>
public static class StateStore
{
    private const string Dir = ".harness";
    private const string FilePath = ".harness/state.json";

    /// <summary>
    /// Final frozen state of the last completed refinement. Exists for the same reason as
    /// <see cref="Trace.LastRunPath"/>: any flow's <c>start</c> resets the live
    /// <c>state.json</c>, so evaluation (which checks completeness) needs to read the
    /// domain keys from a stable snapshot, not the file its own <c>start</c> zeroed out.
    /// </summary>
    public const string LastRunStatePath = ".harness/last-run.state.json";

    /// <summary>Final frozen state of the last evaluation run — its own path, doesn't overwrite refinement's.</summary>
    public const string LastEvaluationStatePath = ".harness/last-evaluation.state.json";

    /// <summary>
    /// Conventional key in <see cref="HarnessState.Data"/> for the label that
    /// <see cref="TaskRegistry"/> propagates to <see cref="Trace"/> on every step (see
    /// <see cref="TraceEntry.Label"/>). Deliberately generic: the engine doesn't know what
    /// a "feature" is — it only re-reads this key if the flow has set it (e.g.
    /// DevelopmentTasks.Pick).
    /// </summary>
    public const string TraceLabelKey = "trace_label";

    public static HarnessState Load() => LoadFrom(FilePath);

    /// <summary>Loads a state from an arbitrary path (e.g. a golden-set case's evidence).</summary>
    public static HarnessState LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize(json, HarnessJsonContext.Default.HarnessState);
                if (state is not null)
                    return state with { Data = state.Data ?? new() };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StateStore] failed to load: {ex.Message}");
        }

        return new HarnessState(0, new());
    }

    public static void Save(HarnessState state)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            AtomicIO.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(state, HarnessJsonContext.Default.HarnessState));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StateStore] failed to save: {ex.Message}");
        }
    }

    public static void Reset() => Save(new HarnessState(0, new()));

    /// <summary>Freezes the live <c>state.json</c> at the destination — the evidence of the completed run.</summary>
    public static void Snapshot(string destination)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Directory.CreateDirectory(Dir);
                AtomicIO.CopyAtomic(FilePath, destination);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StateStore] failed to freeze: {ex.Message}");
        }
    }

    public static int Increment()
    {
        var state = Load();
        var next = state.Step + 1;
        Save(state with { Step = next });
        return next;
    }

    /// <summary>
    /// Adds the turn's cost to the run's accumulator and returns the total — input for the
    /// cost ceiling in <see cref="TaskRegistry"/>. UTF-8 octets of the emitted instruction
    /// are the only measure (not .NET chars — see RFC Appendix B item 1): it's what the
    /// engine can attest on its own, without relying on the driver's self-report, with the
    /// same meaning across engines.
    /// </summary>
    public static int AddCost(int chars)
    {
        var state = Load();
        var next = state with { CostChars = state.CostChars + chars };
        Save(next);
        return next.CostChars;
    }

    public static void Set(string key, string value)
    {
        var state = Load();
        state.Data[key] = value;
        Save(state);
    }

    public static string? Get(string key)
    {
        var state = Load();
        return state.Data.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>Persists the driver context captured on <c>start</c> (see TaskRegistry).</summary>
    public static void SetContext(Dictionary<string, string> context)
    {
        var state = Load();
        Save(state with { Context = context });
    }

    /// <summary>Persisted driver context, for PromptFormatter to reinject into every output.</summary>
    public static Dictionary<string, string>? GetContext() => Load().Context;

    /// <summary>Latches a hard-stop reason across process boundaries.</summary>
    public static void MarkTerminal(string reason)
    {
        var state = Load();
        Save(state with { TerminalReason = reason });
    }

    /// <summary>Clears a recoverable timeout latch after an explicit start.</summary>
    public static void ClearTerminal()
    {
        var state = Load();
        if (state.TerminalReason is not null)
            Save(state with { TerminalReason = null });
    }

    public static string? TerminalReason() => Load().TerminalReason;
}
