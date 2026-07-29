using Harness.Engine;

namespace Flows.Development;

/// <summary>
/// Long-running development flow ("Effective harnesses for long-running agents" pattern,
/// Anthropic). An initializer (session 0) expands the brief into a prioritized feature
/// list; then a loop of fresh-context sessions implements ONE feature at a time:
///
///   start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
///
/// The state that survives the hard resets lives in persistent artifacts: the
/// <see cref="FeatureStore"/> (feature_list.json, the harness's) and progress.txt + git
/// (the target directory's). Each task only performs effects and decides the NEXT
/// command (the <c>output</c> Envelope) — orchestration (dispatch, global guards, transport)
/// lives in Harness.Engine.
///
/// Prompts live in <c>DevelopmentTasks.Prompt.cs</c> (partial).
/// </summary>
public static partial class DevelopmentTasks
{
    // Local guards for this flow (harness.json's global ceiling, 12, is too short for a
    // loop). Few features + a per-feature step ceiling: bars the implement↔verify loop
    // that never closes.
    public const int MaxFeatures = 10;
    public const int StepsPerFeature = 8;

    // Effective step ceiling passed to HarnessHost (override of the global one): slack for
    // the worst case of MaxFeatures features spending StepsPerFeature each, plus
    // start/plan and the boundaries.
    public const int StepBudget = MaxFeatures * StepsPerFeature + 8;

    // StateStore.Data keys used by this flow's partial files (Handoff/Prompt/Verify) — a
    // const instead of a repeated string literal, so a typo in any of these files becomes
    // a compile error instead of a key that's never read.
    private const string CurrentFeatureIdKey = "current_feature_id";
    private const string CurrentFeatureTitleKey = "current_feature_title";
    private const string CurrentFeatureSummaryKey = "current_feature_summary";
    private const string CurrentFeatureVerifyKey = "current_feature_verify";
    private const string FeatureStepsKey = "feature_steps";

    // Name of the brief artifact in ArtifactStore (.harness/brief.md) — persisted in
    // Start() so it can be reinjected into bearings/implement (DevelopmentTasks.Prompt.cs),
    // since the content read from docs/ used to exist only as a local variable of the
    // initializer's turn.
    private const string BriefArtifactName = "brief";

    private static string State(string key) => StateStore.Get(key) ?? "";
    private static string DocsFolder => HarnessConfig.Current.DocsFolder;

    public static string Start()
    {
        // A previous session (maybe from another driver — tokens ran out in one IDE and
        // another takes over) may have died mid-feature. Restarting would throw away work
        // in progress; resuming is safe and deterministic: Bearings is reentrant by
        // construction (it only rearms the per-feature guard) and the next Pick()
        // reselects the same feature, still pending — without needing to know exactly
        // where the previous session stopped.
        if (FeatureStore.PendingCount() > 0)
        {
            Console.Error.WriteLine(
                "[dev] run in progress detected (pending feature); resuming via bearings instead of resetting.");
            return BearingsPrompt();
        }

        // Flow that PRODUCES feature_list: a new run erases the previous run's.
        FeatureStore.Reset();
        RunConfigStore.Reset();
        // Without this, a new run in interactive mode (no docs/) would silently inherit
        // the brief.md from a previous run — interactive mode never calls
        // ArtifactStore.Write, so only this Reset guarantees no brief from an old topic
        // survives.
        ArtifactStore.Reset();

        // Brief (what to build) comes from docs/ or, without docs, from interactive mode.
        if (!DocsReader.HasDocs(DocsFolder))
            return InitializerInteractive();

        var (content, files) = DocsReader.Read(DocsFolder);
        // Persisted so it can be reinjected into bearings/implement
        // (DevelopmentTasks.Prompt.cs) — before this feature, "content" was only a local
        // variable of this turn, discarded as soon as the initializer finished.
        ArtifactStore.Write(BriefArtifactName, content);
        StateStore.Set("origem", "docs");
        return InitializerPrompt(content, files);
    }

    public static string Plan(Envelope? envelope)
    {
        var features = FeatureStore.Parse(Arg(envelope));
        if (features.Count == 0)
            return PlanRetryPrompt(); // couldn't parse → re-request (corrective loop)

        // Feature ceiling: keeps the highest-priority ones (lowest number).
        var capped = features.OrderBy(f => f.Priority).ThenBy(f => f.Id).Take(MaxFeatures).ToList();

        // Sanitize DependsOn: a surviving feature may depend on an id cut above, which
        // would block it forever (never "ready") with no way for the driver to know — the
        // harness did the cutting, not it. Cutting nodes from an already-acyclic graph
        // (validated in FeatureStore.Parse) can't create a cycle, so only cleaning up
        // dangling references is needed.
        var cappedIds = capped.Select(f => f.Id).ToHashSet();
        capped = [.. capped.Select(f => f with { DependsOn = f.Deps.Where(cappedIds.Contains).ToArray() })];

        FeatureStore.Write(capped);

        // Verify command, target directory, and run identity: rehydrated on every
        // smoke/verify step. Kept out of state.json on purpose - see RunConfigStore. RunId
        // is born here (the same instant Start() decided this is a new run, not a resumed
        // one) and survives every subsequent session without needing to appear in the
        // Envelope exchanged with the model (RFC §6.4 — run identity is a control-plane
        // concern, not part of the contract).
        RunConfigStore.Write(new RunConfig(
            ArgAt(envelope, 1, "dotnet test"),
            ArgAt(envelope, 2, "."),
            Guid.NewGuid().ToString()));

        return BearingsPrompt();
    }

    public static string Bearings(Envelope? envelope)
    {
        // New session (one feature): resets the per-feature guard counter.
        StateStore.Set(FeatureStepsKey, "1");
        return SmokePrompt();
    }

    public static string Smoke(Envelope? envelope) =>
        OverFeatureBudget() ? Stop("per-feature guard") : PickPrompt();

    public static string Pick(Envelope? envelope)
    {
        if (OverFeatureBudget())
            return Stop("per-feature guard");

        // DETERMINISTIC selection: highest priority among the ready ones (dependencies
        // satisfied). The harness chooses, not the LLM.
        var next = FeatureStore.NextPending();
        if (next is null)
        {
            // PendingCount() == 0 is the normal case (handoff would already have closed it
            // before). A pending count > 0 is only reachable via a feature_list.json
            // hand-edited outside the graph validated in plan (Write/MarkPassed don't
            // revalidate) — doesn't fake success in that case.
            return FeatureStore.PendingCount() == 0
                ? Done()
                : Stop("blocked dependencies — no pending feature is ready");
        }

        StateStore.Set(CurrentFeatureIdKey, next.Id.ToString());
        StateStore.Set(CurrentFeatureTitleKey, next.Title);
        // Tags the trace with the current feature (see TraceEntry.Label) — without this,
        // every trace.jsonl line only has the global Step, without saying which feature it
        // belongs to.
        StateStore.Set(StateStore.TraceLabelKey, $"feature:{next.Id}");
        return ImplementPrompt(next);
    }

    public static string Implement(Envelope? envelope)
    {
        if (OverFeatureBudget())
            return Stop("per-feature guard");

        var summary = Arg(envelope).Trim();
        if (!string.IsNullOrWhiteSpace(summary))
            StateStore.Set(CurrentFeatureSummaryKey, summary);

        var autoVerify = TryAutomatedVerify();
        if (autoVerify.Attempted)
        {
            StateStore.Set(CurrentFeatureVerifyKey, autoVerify.Result);
            return autoVerify.Success
                ? CompleteVerifiedFeature(autoVerify.Result)
                : FixPrompt(autoVerify.Result);
        }

        return VerifyPrompt();
    }

    public static string Verify(Envelope? envelope)
    {
        if (OverFeatureBudget())
            return Stop("per-feature guard");

        // FAILED → back to implementing the SAME feature (correction loop, bounded by the
        // guard). PASSED → the harness does the deterministic handoff (progress + git)
        // without spending a model turn; if it fails, falls back to the legacy
        // manual-repair prompt.
        var result = Arg(envelope).Trim();
        if (result.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase))
            return FixPrompt(result);

        if (result.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
        {
            StateStore.Set(CurrentFeatureVerifyKey, result);
            return CompleteVerifiedFeature(result);
        }

        return VerifyRetryPrompt();
    }

    public static string Handoff(Envelope? envelope)
    {
        if (string.IsNullOrWhiteSpace(Arg(envelope)))
            return HandoffRetryPrompt();

        if (int.TryParse(State(CurrentFeatureIdKey), out var id))
            FeatureStore.MarkPassed(id);

        // Any feature still pending? Yes → next session (bearings). No → done.
        return FeatureStore.AllPassing() ? Done() : BearingsPrompt();
    }

    // --- guards and termination -------------------------------------------------

    /// <summary>Increments the session counter and signals a per-feature ceiling overrun.</summary>
    private static bool OverFeatureBudget()
    {
        var steps = (int.TryParse(State(FeatureStepsKey), out var s) ? s : 0) + 1;
        StateStore.Set(FeatureStepsKey, steps.ToString());

        if (steps > StepsPerFeature)
        {
            Console.Error.WriteLine(
                $"[dev] feature '{State(CurrentFeatureTitleKey)}' exceeded {StepsPerFeature} steps; stopping.");
            return true;
        }
        return false;
    }

    private static string Stop(string reason)
    {
        Console.Error.WriteLine($"[dev] stopped due to {reason}. feature_list in .harness/feature_list.json");
        return "stop";
    }

    private static string Done()
    {
        Console.Error.WriteLine(
            $"[dev] all {FeatureStore.Load().Count} features pass; done. "
            + "State in .harness/feature_list.json");
        return "stop";
    }

    private static string Arg(Envelope? envelope) =>
        envelope?.Args is { Length: > 0 } ? envelope.Args[0] : string.Empty;

    private static string ArgAt(Envelope? envelope, int index, string fallback) =>
        envelope?.Args is { } args && args.Length > index && !string.IsNullOrWhiteSpace(args[index])
            ? args[index]
            : fallback;
}
