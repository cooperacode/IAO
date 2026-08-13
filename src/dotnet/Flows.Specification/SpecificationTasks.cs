using Harness.Engine;

namespace Flows.Specification;

/// <summary>
/// Long-running Specification flow (blueprint 0004 §2 "State machine"): idea → PRD → ... →
/// approval → publish. This slice implements the happy-path prefix only:
///
/// start → discover → product → stop
///
/// Later phases (analysis, design, review, approve, publish) are out of scope for this
/// feature — <c>Product()</c> stops the chain once <c>prd.accepted.json</c> is written.
///
/// Each task only performs effects and decides the NEXT command (the <c>output</c> Envelope);
/// orchestration (dispatch, global guards, transport) lives in Harness.Engine, dispatched via
/// <see cref="Harness.Engine.TaskRegistry"/>/<see cref="Harness.Engine.HarnessHost"/> — this
/// flow does not reimplement the loop.
///
/// Prompts live in <c>SpecificationTasks.Prompt.cs</c> (partial).
/// </summary>
public static partial class SpecificationTasks
{
    /// <summary>
    /// Effective step ceiling passed to HarnessHost — "budget inicial sugerido: 18 passos,
    /// como no flow histórico" (blueprint 0004 §2).
    /// </summary>
    public const int StepBudget = 18;

    /// <summary>
    /// Emits the discover prompt for a fresh run, or resumes from the persisted phase
    /// (blueprint 0004 §2 tabela: <c>start</c> → "Prompt discover ou resume da fase
    /// persistida"). A run is only genuinely new when there's no run in progress at
    /// <c>discover</c>/<c>product</c> — a resumed run must not lose the already-accepted
    /// idea/PRD or restart the state machine from the top.
    /// </summary>
    public static string Start()
    {
        var run = SpecificationStore.LoadRun();

        if (run.Status == "in_progress" && run.Phase is "discover" or "product")
        {
            HarnessLog.Info($"[spec] run in progress detected (phase={run.Phase}); resuming.");
            return run.Phase == "product" ? ProductPrompt() : DiscoverPrompt();
        }

        // Fresh run (or a previous run reached a terminal state): the previous run's
        // proposals/accepted artifacts must not leak into this one.
        SpecificationStore.Reset();
        SpecificationStore.SaveRun(new RunState(0, "in_progress", "discover", new(), null, null));
        return DiscoverPrompt();
    }

    /// <summary>
    /// Validates the idea proposal with <see cref="SpecificationEvaluator.EvaluateIdea"/>. On
    /// failure, retries in place (same command, violations reported). On pass, persists
    /// <c>idea.accepted.json</c> and advances to <c>product</c>.
    /// </summary>
    public static string Discover(Envelope? envelope)
    {
        var idea = SpecificationStore.ReadProposal(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        if (idea is null)
            return DiscoverRetryPrompt([
                $"no readable idea proposal was found at '{IdeaProposalPath}' (missing or not valid JSON)."
            ]);

        // Source-document ingestion is out of scope for this slice: a fixed provenance tag
        // plus a digest of the idea's own canonical content satisfies IdeaEvaluator's
        // "fonte e digest registrados" predicate without inventing new persistence.
        var sourceDigest = SpecificationStore.DigestOf(idea, SpecificationJsonContext.Default.IdeaFrame);
        var evaluation = SpecificationEvaluator.EvaluateIdea(idea, source: "driver", sourceDigest: sourceDigest);
        if (!evaluation.Passed)
            return DiscoverRetryPrompt(evaluation.Violations.Select(v => $"{v.Code}: {v.Message}"));

        SpecificationStore.WriteAccepted(
            SpecificationStore.Phases.Idea, idea, SpecificationJsonContext.Default.IdeaFrame);
        SpecificationStore.SaveRun(new RunState(StateStore.Load().Step, "in_progress", "product", new(), null, null));
        return ProductPrompt();
    }

    /// <summary>
    /// Validates the PRD proposal with <see cref="SpecificationEvaluator.EvaluatePrd"/> against
    /// the currently accepted idea's digest. On failure, retries in place (same command,
    /// violations reported). On pass, persists <c>prd.accepted.json</c>, marks the run
    /// completed, and stops — this feature's happy path ends here.
    /// </summary>
    public static string Product(Envelope? envelope)
    {
        var prd = SpecificationStore.ReadProposal(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        if (prd is null)
            return ProductRetryPrompt([
                $"no readable PRD proposal was found at '{PrdProposalPath}' (missing or not valid JSON)."
            ]);

        var (_, ideaDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);
        var evaluation = SpecificationEvaluator.EvaluatePrd(prd, ideaDigest ?? "");
        if (!evaluation.Passed)
            return ProductRetryPrompt(evaluation.Violations.Select(v => $"{v.Code}: {v.Message}"));

        SpecificationStore.WriteAccepted(
            SpecificationStore.Phases.Prd, prd, SpecificationJsonContext.Default.PrdDocument);
        SpecificationStore.SaveRun(new RunState(StateStore.Load().Step, "completed", "stop", new(), null, null));
        HarnessLog.Info("[spec] discover→product completed; prd.accepted.json written.");
        return "stop";
    }
}
