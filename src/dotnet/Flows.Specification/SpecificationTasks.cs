using Harness.Engine;

namespace Flows.Specification;

/// <summary>
/// Long-running Specification flow (blueprint 0004 §2 "State machine"): idea → PRD → ... →
/// approval → publish. This slice implements the happy-path prefix:
///
/// start → discover → product → analysis → design → review → stop (awaiting_approval)
///
/// review's recascade routing (blueprint 0006 review state machine) can send the run back
/// through product/analysis/design before it reaches review again — capped at two recascades
/// per run (blueprint 0004 §2 budget note); a third failing verdict is a terminal
/// <c>needs_human_decision</c>, not another retry. Later phases (approve, publish) are out of
/// scope for this feature.
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

        if (run.Status == "in_progress" && run.Phase is "discover" or "product" or "analysis" or "design" or "review")
        {
            HarnessLog.Info($"[spec] run in progress detected (phase={run.Phase}); resuming.");
            return run.Phase switch
            {
                "product" => ProductPrompt(),
                "analysis" => AnalysisPrompt(),
                "design" => DesignPrompt(),
                "review" => ReviewPrompt(),
                _ => DiscoverPrompt(),
            };
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
        var runAfterDiscover = SpecificationStore.LoadRun();
        SpecificationStore.SaveRun(runAfterDiscover with { Step = StateStore.Load().Step, Status = "in_progress", Phase = "product" });
        return ProductPrompt();
    }

    /// <summary>
    /// Validates the PRD proposal with <see cref="SpecificationEvaluator.EvaluatePrd"/> against
    /// the currently accepted idea's digest. On failure, retries in place (same command,
    /// violations reported). On pass, persists <c>prd.accepted.json</c> and advances to
    /// <c>analysis</c>.
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
        var runAfterProduct = SpecificationStore.LoadRun();
        SpecificationStore.SaveRun(runAfterProduct with { Step = StateStore.Load().Step, Status = "in_progress", Phase = "analysis" });
        HarnessLog.Info("[spec] product accepted; prd.accepted.json written; advancing to analysis.");
        return AnalysisPrompt();
    }

    /// <summary>
    /// Validates the SRS proposal with <see cref="SpecificationEvaluator.EvaluateSrs"/> against
    /// the currently accepted PRD's digest and goal ids. On failure, retries in place (same
    /// command, violations reported). On pass, persists <c>srs.accepted.json</c> and advances
    /// to <c>design</c>.
    /// </summary>
    public static string Analysis(Envelope? envelope)
    {
        var srs = SpecificationStore.ReadProposal(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        if (srs is null)
            return AnalysisRetryPrompt([
                $"no readable SRS proposal was found at '{SrsProposalPath}' (missing or not valid JSON)."
            ]);

        var (prd, prdDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        var acceptedGoalIds = prd?.Goals.Select(g => g.Id).ToArray() ?? [];
        var evaluation = SpecificationEvaluator.EvaluateSrs(srs, prdDigest ?? "", acceptedGoalIds);
        if (!evaluation.Passed)
            return AnalysisRetryPrompt(evaluation.Violations.Select(v => $"{v.Code}: {v.Message}"));

        SpecificationStore.WriteAccepted(
            SpecificationStore.Phases.Srs, srs, SpecificationJsonContext.Default.SoftwareSpecification);
        var runAfterAnalysis = SpecificationStore.LoadRun();
        SpecificationStore.SaveRun(runAfterAnalysis with { Step = StateStore.Load().Step, Status = "in_progress", Phase = "design" });
        HarnessLog.Info("[spec] analysis accepted; srs.accepted.json written; advancing to design.");
        return DesignPrompt();
    }

    /// <summary>
    /// Validates the SDD proposal with <see cref="SpecificationEvaluator.EvaluateSdd"/> against
    /// the currently accepted SRS's digest and requirement ids. On failure, retries in place
    /// (same command, violations reported). On pass, persists <c>sdd.accepted.json</c> and
    /// advances to <c>review</c> (blueprint 0006 review phase) instead of stopping.
    /// </summary>
    public static string Design(Envelope? envelope)
    {
        var sdd = SpecificationStore.ReadProposal(
            SpecificationStore.Phases.Sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        if (sdd is null)
            return DesignRetryPrompt([
                $"no readable SDD proposal was found at '{SddProposalPath}' (missing or not valid JSON)."
            ]);

        var (srs, srsDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        var requirementIds = srs is null
            ? []
            : srs.FunctionalRequirements.Concat(srs.QualityRequirements).Select(r => r.Id).ToArray();
        var evaluation = SpecificationEvaluator.EvaluateSdd(sdd, srsDigest ?? "", requirementIds);
        if (!evaluation.Passed)
            return DesignRetryPrompt(evaluation.Violations.Select(v => $"{v.Code}: {v.Message}"));

        SpecificationStore.WriteAccepted(
            SpecificationStore.Phases.Sdd, sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        var runAfterDesign = SpecificationStore.LoadRun();
        SpecificationStore.SaveRun(runAfterDesign with { Step = StateStore.Load().Step, Status = "in_progress", Phase = "review" });
        HarnessLog.Info("[spec] design accepted; sdd.accepted.json written; advancing to review.");
        return ReviewPrompt();
    }

    /// <summary>
    /// Validates the readiness verdict proposal with
    /// <see cref="SpecificationEvaluator.EvaluateReadiness"/> against the currently accepted
    /// SRS's requirement ids and the currently accepted SDD's ADR ids. On structural failure,
    /// retries <c>review</c> in place (same command, violations reported), same as every other
    /// phase.
    ///
    /// On a structurally valid <c>READY</c> verdict: persists <c>readiness.accepted.json</c> and
    /// pauses for human approval (<c>Status = "awaiting_approval"</c>, <c>Phase = "approve"</c> —
    /// blueprint 0004 §2 lists <c>awaiting_approval</c> as a real status value; wiring the
    /// <c>approve</c> phase itself is a later feature).
    ///
    /// On a structurally valid <c>FAIL:product</c>/<c>FAIL:analysis</c>/<c>FAIL:design</c>
    /// verdict: recascades back to the exact failing phase (blueprint 0006 review state
    /// machine) — that phase's own success path already walks forward through the remaining
    /// phases to <c>review</c> again, so nothing else needs to be reimplemented here. Capped at
    /// two recascades per run (blueprint 0004 §2 budget note: "no máximo duas recascatas"); a
    /// third failing verdict is a terminal <c>needs_human_decision</c>, not another retry.
    /// </summary>
    public static string Review(Envelope? envelope)
    {
        var verdict = SpecificationStore.ReadProposal(
            SpecificationStore.Phases.Review, SpecificationJsonContext.Default.ReadinessVerdict);
        if (verdict is null)
            return ReviewRetryPrompt([
                $"no readable readiness verdict proposal was found at '{ReviewProposalPath}' (missing or not valid JSON)."
            ]);

        var (srs, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        var knownRequirementIds = srs is null
            ? []
            : srs.FunctionalRequirements.Concat(srs.QualityRequirements).Select(r => r.Id).ToArray();

        var (sdd, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        var knownAdrIds = sdd is null ? [] : sdd.Adrs.Select(a => a.Id).ToArray();

        var evaluation = SpecificationEvaluator.EvaluateReadiness(verdict, knownRequirementIds, knownAdrIds);
        if (!evaluation.Passed)
            return ReviewRetryPrompt(evaluation.Violations.Select(v => $"{v.Code}: {v.Message}"));

        var run = SpecificationStore.LoadRun();

        if (verdict.Verdict == "READY")
        {
            SpecificationStore.WriteAccepted(
                SpecificationStore.Phases.Readiness, verdict, SpecificationJsonContext.Default.ReadinessVerdict);
            SpecificationStore.SaveRun(run with { Status = "awaiting_approval", Phase = "approve" });
            HarnessLog.Info("[spec] review READY; readiness.accepted.json written; awaiting approval.");
            return "stop";
        }

        // FAIL:product | FAIL:analysis | FAIL:design — EvaluateReadiness already restricted
        // verdict.Verdict to the allowed set, so this is always one of those three here.
        var targetPhase = verdict.Verdict["FAIL:".Length..];
        var recascadeCount = run.Counters.GetValueOrDefault("recascades", 0);

        if (recascadeCount >= 2)
        {
            SpecificationStore.SaveRun(run with { Status = "needs_human_decision", TerminalReason = "recascade limit reached" });
            HarnessLog.Info($"[spec] review {verdict.Verdict}; recascade limit (2) already reached; stopping for human decision.");
            return "stop";
        }

        // Copy rather than mutate run.Counters in place — records don't deep-clone their
        // reference-typed members, so writing through the loaded instance would corrupt
        // whatever else still holds a reference to it.
        var counters = new Dictionary<string, int>(run.Counters) { ["recascades"] = recascadeCount + 1 };
        SpecificationStore.SaveRun(run with { Status = "in_progress", Phase = targetPhase, Counters = counters });
        HarnessLog.Info($"[spec] review {verdict.Verdict}; recascading to '{targetPhase}' (attempt {recascadeCount + 1}/2).");

        return targetPhase switch
        {
            "product" => ProductPrompt(),
            "analysis" => AnalysisPrompt(),
            "design" => DesignPrompt(),
            _ => ReviewRetryPrompt([$"READINESS_VERDICT_INVALID: unroutable verdict '{verdict.Verdict}'"]),
        };
    }
}
