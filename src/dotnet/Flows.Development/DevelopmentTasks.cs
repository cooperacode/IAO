using Harness.Engine;

namespace Flows.Development;

/// <summary>
/// Long-running development flow ("Effective harnesses for long-running agents" pattern,
/// Anthropic). An initializer (session 0) expands the brief into a prioritized feature
/// list; then a loop of fresh-context sessions implements ONE feature at a time:
///
///   start → plan → [implement → verify(auto-handoff)]*
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
    private const string BearingsKey = "current_bearings";

    // Name of the brief artifact in ArtifactStore (.harness/brief.md) — persisted in
    // Start() so it can be reinjected into the implement prompt,
    // since the content read from docs/ used to exist only as a local variable of the
    // initializer's turn.
    private const string BriefArtifactName = "brief";

    private static string State(string key) => StateStore.Get(key) ?? "";
    private static string DocsFolder => HarnessConfig.Current.DocsFolder;

    public static string Start()
    {
        // A previous session (maybe from another driver — tokens ran out in one IDE and
        // another takes over) may have died mid-feature. Restarting would throw away work
        // in progress; resuming is safe and deterministic: the harness reconstructs the
        // session context and reselects the same pending feature without needing to know
        // exactly where the previous session stopped.
        if (FeatureStore.PendingCount() > 0)
        {
            Console.Error.WriteLine(
                "[dev] run in progress detected (pending feature); resuming deterministically.");
            return Bearings(null);
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
        // Persisted so it can be reinjected into the deterministic bearings context and
        // implementation prompt
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
            ExternalOrArg("HARNESS_VERIFY_CMD", envelope, 1, "dotnet test"),
            ExternalOrArg("HARNESS_TARGET_DIR", envelope, 2, "."),
            Guid.NewGuid().ToString()));

        // Bearings, smoke and pick are deterministic harness work. The first driver turn
        // after planning should be the creative implementation turn.
        return Bearings(null);
    }

    public static string Bearings(Envelope? envelope)
    {
        // New session (one feature): resets the per-feature guard counter and captures
        // bounded repository evidence without spending a model turn.
        StateStore.Set(FeatureStepsKey, "1");
        CaptureBearings();
        return Smoke(null);
    }

    public static string Smoke(Envelope? envelope)
    {
        if (OverFeatureBudget())
            return Stop("per-feature guard");

        var smoke = TryAutomatedSmoke();
        if (!smoke.Success)
            return SmokeRetryPrompt(smoke.Result);

        // Selection is already deterministic; no driver acknowledgement is required.
        return Pick(null);
    }

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

        // The driver's prose summary is not control-plane evidence. Derive the progress
        // description from the actual change set and keep the response payload optional.
        StateStore.Set(CurrentFeatureSummaryKey, ImplementationSummary());

        var autoVerify = TryDeterministicVerify();
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

        // The response payload is compatibility-only. The harness reruns the configured
        // verification command and decides from its process result, never from a textual
        // PASS/FAIL asserted by the driver.
        var result = TryDeterministicVerify();
        if (result.Attempted)
        {
            StateStore.Set(CurrentFeatureVerifyKey, result.Result);
            return result.Success
                ? CompleteVerifiedFeature(result.Result)
                : FixPrompt(result.Result);
        }

        return VerifyRetryPrompt();
    }

    public static string Handoff(Envelope? envelope)
    {
        var verified = State(CurrentFeatureVerifyKey);
        if (!verified.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
            return VerifyRetryPrompt();

        // A driver-supplied hash is not evidence. Retry the actual deterministic handoff
        // and inspect the repository state before marking the feature as passed.
        var handoff = TryAutomatedHandoff(verified);
        if (!handoff.Success)
            return HandoffPrompt(handoff.Failure);

        if (int.TryParse(State(CurrentFeatureIdKey), out var id))
            FeatureStore.MarkPassed(id);

        // Any feature still pending? Yes → next session. No → done.
        return FeatureStore.AllPassing() ? Done() : Bearings(null);
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

    private static string ExternalOrArg(string variable, Envelope? envelope, int index, string fallback)
    {
        var external = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(external) ? external : ArgAt(envelope, index, fallback);
    }

    private static void CaptureBearings()
    {
        var targetDir = RunConfigStore.Load().TargetDir;
        string resolvedTarget;
        try
        {
            resolvedTarget = ResolveTargetDir(targetDir);
        }
        catch (InvalidOperationException ex)
        {
            StateStore.Set(BearingsKey, $"cwd: {Directory.GetCurrentDirectory()}\nTarget error: {ex.Message}");
            return;
        }

        var progressPath = Path.Combine(resolvedTarget, "progress.txt");
        string progress;
        try
        {
            progress = File.Exists(progressPath)
                ? string.Join("\n", File.ReadAllLines(progressPath).TakeLast(12))
                : "(progress.txt not found)";
        }
        catch (Exception ex)
        {
            progress = $"(progress unavailable: {OneLine(ex.Message)})";
        }

        var git = GitCommand.Run(resolvedTarget, "log", "-n", "10", "--oneline");
        var gitLog = git.ExitCode == 0
            ? git.Output.Trim()
            : $"(git log unavailable: {OneLine(git.Error, "not a Git repository")})";

        StateStore.Set(BearingsKey,
            $"cwd: {Directory.GetCurrentDirectory()}\n"
            + $"target: {resolvedTarget}\n"
            + $"progress tail:\n{progress}\n"
            + $"recent git log:\n{gitLog}");
    }

    private static string ImplementationSummary()
    {
        try
        {
            var targetDir = ResolveTargetDir(RunConfigStore.Load().TargetDir);
            var staged = GitCommand.Run(
                targetDir, "diff", "--cached", "--stat", "--", ".", ":(exclude).harness");
            var unstaged = GitCommand.Run(
                targetDir, "diff", "--stat", "--", ".", ":(exclude).harness");
            var summary = OneLine($"{staged.Output} {unstaged.Output}");
            return string.IsNullOrWhiteSpace(summary)
                ? "implementation completed"
                : $"changed: {Snippet(summary)}";
        }
        catch
        {
            return "implementation completed";
        }
    }
}
