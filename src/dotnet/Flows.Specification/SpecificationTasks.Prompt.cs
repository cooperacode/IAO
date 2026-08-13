using Harness.Engine;

namespace Flows.Specification;

/// <summary>
/// Builds the Specification flow's prompts — the "strategy" kept separate from the state
/// machine in <c>SpecificationTasks.cs</c> (same split as
/// <c>Flows.Development/DevelopmentTasks.Prompt.cs</c>).
/// </summary>
public static partial class SpecificationTasks
{
    // Known-convention paths under SpecificationStore's namespaced directory (blueprint 0004
    // §4). SpecificationStore keeps its proposal/accepted path builders private; the prompts
    // only need to tell the driver where to write, so this mirrors the documented convention
    // directly instead of adding a public accessor for a single call site's sake.
    private const string IdeaProposalPath = ".harness/specification/active/idea.proposal.json";
    private const string PrdProposalPath = ".harness/specification/active/prd.proposal.json";
    private const string SrsProposalPath = ".harness/specification/active/srs.proposal.json";
    private const string SddProposalPath = ".harness/specification/active/sdd.proposal.json";
    private const string ReviewProposalPath = ".harness/specification/active/review.proposal.json";
    private const string ApprovalProposalPath = ".harness/specification/active/approval.proposal.json";

    private const string IdeaShape =
        """{"schema":"iao/idea/v1","title":"...","problem":"...","users":["..."],"desiredOutcomes":["..."],"constraints":["..."],"openQuestions":[{"id":"OQ-1","question":"...","blocking":false}]}""";

    private const string PrdShape =
        """{"schema":"iao/prd/v1","ideaDigest":"sha256:...","vision":"...","goals":[{"id":"G-1","statement":"..."}],"successMetrics":[{"id":"M-1","goalId":"G-1","measure":"...","target":"..."}],"nonGoals":["..."],"scope":["..."],"risks":[{"id":"R-1","description":"...","mitigation":"...","severity":"..."}],"decisions":[{"id":"D-1","statement":"...","rationale":"..."}],"openQuestions":[]}""";

    private const string SrsShape =
        """{"schema":"iao/srs/v1","prdDigest":"sha256:...","functionalRequirements":[{"id":"RF-1","goalIds":["G-1"],"statement":"...","dependsOn":[],"acceptanceIds":["AC-1"]}],"qualityRequirements":[],"acceptanceCriteria":[{"id":"AC-1","requirementIds":["RF-1"],"given":"...","when":"...","then":"..."}],"interfaces":[],"dataRules":[],"delivery":{"target":"...","verificationStrategy":"...","isBootstrap":true}}""";

    private const string SddShape =
        """{"schema":"iao/sdd/v1","srsDigest":"sha256:...","adrs":[{"id":"ADR-1","title":"...","decision":"...","rationale":"...","requirementIds":["RF-1"]}],"controls":[{"id":"IC-1","name":"...","description":"...","requirementIds":["RF-1"]}]}""";

    private const string ReviewShape =
        """{"verdict":"READY","slices":[{"id":"SL-1","classification":"...","goal":"...","inScope":["..."],"outOfScope":["..."],"observableOutcome":"...","requirementIds":["RF-1"],"adrIds":["ADR-1"],"dependsOn":[],"contracts":["..."],"happyPath":"...","failurePath":"...","acceptanceCriterion":"...","suggestedTarget":"...","suggestedVerificationStrategy":"..."}],"conflicts":[],"residuals":[]}""";

    private const string ApprovalShape =
        """{"decision":"approved","bundleDigest":"sha256:...","rationale":"...","approvedBy":"...","decidedAt":"2026-01-01T00:00:00Z"}""";

    // --- discover ---------------------------------------------------------

    private static string DiscoverPrompt() =>
        PromptFormatter.Format(
            input: $"""
            Frame the idea for this Specification run (blueprint 0004 §2/§3, discover phase).

            Write a JSON OBJECT to the file '{IdeaProposalPath}' (a real file, written with your
            file-write tool — NOT escaped or embedded inside the envelope you send back) with this
            shape: {IdeaShape}
            `schema` must be exactly "{SpecificationEvaluator.IdeaSchema}". Provide a title, a
            problem statement, at least one user and one desired outcome; keep the canonical JSON
            under {SpecificationEvaluator.MaxIdeaUtf8Bytes} UTF-8 bytes and give every open
            question a unique id.

            Return `discover` without arguments when done; the harness will validate the file and
            either advance to `product` or re-request `discover` with the reported violations.
            """,
            output: new Envelope(EnvelopeType.Command, "discover", []));

    private static string DiscoverRetryPrompt(IEnumerable<string> violations) =>
        PromptFormatter.Format(
            input: $"""
            The idea proposal at '{IdeaProposalPath}' did not pass IdeaEvaluator:
            {string.Join("\n", violations.Select(v => $"- {v}"))}

            Rewrite the file at the exact same path with this shape: {IdeaShape}
            `schema` must be exactly "{SpecificationEvaluator.IdeaSchema}". Return `discover`
            without arguments for another harness-controlled attempt.
            """,
            output: new Envelope(EnvelopeType.Command, "discover", []));

    // --- product ------------------------------------------------------------

    private static string ProductPrompt()
    {
        var (_, ideaDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);

        return PromptFormatter.Format(
            input: $"""
            Draft the PRD for this Specification run (blueprint 0004 §2/§3, product phase),
            building on the accepted idea (digest '{ideaDigest}').

            Write a JSON OBJECT to the file '{PrdProposalPath}' (a real file, written with your
            file-write tool — NOT escaped or embedded inside the envelope you send back) with this
            shape: {PrdShape}
            `schema` must be exactly "{SpecificationEvaluator.PrdSchema}" and `ideaDigest` must be
            set to exactly '{ideaDigest}'. Provide at least one goal, one non-goal, one scope
            entry and one success metric with a measure and target that are both present and
            distinct; leave no blocking open question unresolved.

            Return `product` without arguments when done; the harness will validate the file and
            either advance to `analysis`, or re-request `product` with the reported violations.
            """,
            output: new Envelope(EnvelopeType.Command, "product", []));
    }

    private static string ProductRetryPrompt(IEnumerable<string> violations)
    {
        var (_, ideaDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Idea, SpecificationJsonContext.Default.IdeaFrame);

        return PromptFormatter.Format(
            input: $"""
            The PRD proposal at '{PrdProposalPath}' did not pass PrdEvaluator:
            {string.Join("\n", violations.Select(v => $"- {v}"))}

            Rewrite the file at the exact same path with this shape: {PrdShape}
            `schema` must be exactly "{SpecificationEvaluator.PrdSchema}" and `ideaDigest` must be
            set to exactly '{ideaDigest}'. Return `product` without arguments for another
            harness-controlled attempt.
            """,
            output: new Envelope(EnvelopeType.Command, "product", []));
    }

    // --- analysis -------------------------------------------------------------

    private static string AnalysisPrompt()
    {
        var (_, prdDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);

        return PromptFormatter.Format(
            input: $"""
            Draft the SRS for this Specification run (blueprint 0004 §2/§3, analysis phase),
            building on the accepted PRD (digest '{prdDigest}').

            Write a JSON OBJECT to the file '{SrsProposalPath}' (a real file, written with your
            file-write tool — NOT escaped or embedded inside the envelope you send back) with this
            shape: {SrsShape}
            `schema` must be exactly "{SpecificationEvaluator.SrsSchema}" and `prdDigest` must be
            set to exactly '{prdDigest}'. Every goal from the accepted PRD must be covered by at
            least one requirement's `goalIds`. Every functional and quality requirement needs a
            unique id and at least one `acceptanceIds` entry that resolves to a real entry in
            `acceptanceCriteria`. Every `dependsOn` id and every acceptance criterion/interface/
            data-rule requirement reference must point at a requirement id that actually exists.

            Return `analysis` without arguments when done; the harness will validate the file and
            either advance to `design`, or re-request `analysis` with the reported violations.
            """,
            output: new Envelope(EnvelopeType.Command, "analysis", []));
    }

    private static string AnalysisRetryPrompt(IEnumerable<string> violations)
    {
        var (_, prdDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);

        return PromptFormatter.Format(
            input: $"""
            The SRS proposal at '{SrsProposalPath}' did not pass SrsEvaluator:
            {string.Join("\n", violations.Select(v => $"- {v}"))}

            Rewrite the file at the exact same path with this shape: {SrsShape}
            `schema` must be exactly "{SpecificationEvaluator.SrsSchema}" and `prdDigest` must be
            set to exactly '{prdDigest}'. Return `analysis` without arguments for another
            harness-controlled attempt.
            """,
            output: new Envelope(EnvelopeType.Command, "analysis", []));
    }

    // --- design ---------------------------------------------------------------

    private static string DesignPrompt()
    {
        var (_, srsDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);

        return PromptFormatter.Format(
            input: $"""
            Draft the SDD for this Specification run (blueprint 0004 §2/§3, design phase),
            building on the accepted SRS (digest '{srsDigest}').

            Write a JSON OBJECT to the file '{SddProposalPath}' (a real file, written with your
            file-write tool — NOT escaped or embedded inside the envelope you send back) with this
            shape: {SddShape}
            `schema` must be exactly "{SpecificationEvaluator.SddSchema}" and `srsDigest` must be
            set to exactly '{srsDigest}'. Every functional and quality requirement from the
            accepted SRS must be allocated to (referenced by) at least one ADR's
            `requirementIds`. Every ADR id must be unique, and every ADR/control requirement
            reference must point at a requirement id that actually exists in the accepted SRS.

            Return `design` without arguments when done; the harness will validate the file and
            either persist sdd.accepted.json and stop, or re-request `design` with the reported
            violations.
            """,
            output: new Envelope(EnvelopeType.Command, "design", []));
    }

    private static string DesignRetryPrompt(IEnumerable<string> violations)
    {
        var (_, srsDigest) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);

        return PromptFormatter.Format(
            input: $"""
            The SDD proposal at '{SddProposalPath}' did not pass SddEvaluator:
            {string.Join("\n", violations.Select(v => $"- {v}"))}

            Rewrite the file at the exact same path with this shape: {SddShape}
            `schema` must be exactly "{SpecificationEvaluator.SddSchema}" and `srsDigest` must be
            set to exactly '{srsDigest}'. Return `design` without arguments for another
            harness-controlled attempt.
            """,
            output: new Envelope(EnvelopeType.Command, "design", []));
    }

    // --- review -----------------------------------------------------------------

    private static string ReviewPrompt() =>
        PromptFormatter.Format(
            input: $"""
            Assess readiness for this Specification run (blueprint 0004 §2/§7, blueprint 0006
            review phase): judge whether the accepted idea/PRD/SRS/SDD chain is internally
            consistent, or whether an earlier phase needs to be redone.

            Write a JSON OBJECT to the file '{ReviewProposalPath}' (a real file, written with your
            file-write tool — NOT escaped or embedded inside the envelope you send back) with this
            shape: {ReviewShape}
            `verdict` must be exactly one of "READY", "FAIL:product", "FAIL:analysis" or
            "FAIL:design". `conflicts` and `residuals` are always required arrays (use `[]` when
            there are none — never omit them).

            If `verdict` is "READY": propose at most 10 readiness slices, each with a unique id.
            Every `requirementIds` entry must reference a real requirement from the accepted SRS
            and every `adrIds` entry must reference a real ADR from the accepted SDD. Every
            `dependsOn` entry must name another slice in this same list (never itself, never an
            id outside this proposal); the dependency graph must be acyclic, and at least one
            slice must have an empty `dependsOn` (a starting slice). Every requirement in the
            accepted SRS must be covered by at least one slice's `requirementIds` — the slices
            must form a complete cover.

            If `verdict` starts with "FAIL:": leave `slices` empty — a FAIL verdict is a
            rejection of an earlier phase, not a slice proposal; explain the rejection through
            `conflicts`/`residuals` instead.

            Return `review` without arguments when done; the harness will validate the file and
            either pause for approval (READY), recascade to the failing phase (FAIL:*), or
            re-request `review` with the reported violations.
            """,
            output: new Envelope(EnvelopeType.Command, "review", []));

    private static string ReviewRetryPrompt(IEnumerable<string> violations) =>
        PromptFormatter.Format(
            input: $"""
            The readiness verdict proposal at '{ReviewProposalPath}' did not pass
            ReadinessEvaluator:
            {string.Join("\n", violations.Select(v => $"- {v}"))}

            Rewrite the file at the exact same path with this shape: {ReviewShape}
            `verdict` must be exactly one of "READY", "FAIL:product", "FAIL:analysis" or
            "FAIL:design", and `conflicts`/`residuals` must always be present arrays. Return
            `review` without arguments for another harness-controlled attempt.
            """,
            output: new Envelope(EnvelopeType.Command, "review", []));

    // --- approve ------------------------------------------------------------------

    private static string BundlePreview()
    {
        var (prd, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Prd, SpecificationJsonContext.Default.PrdDocument);
        var (srs, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Srs, SpecificationJsonContext.Default.SoftwareSpecification);
        var (sdd, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Sdd, SpecificationJsonContext.Default.SoftwareDesignDocument);
        var (readiness, _) = SpecificationStore.ReadAccepted(
            SpecificationStore.Phases.Readiness, SpecificationJsonContext.Default.ReadinessVerdict);

        return $"""
            ## Preview — bundle to be published to specs/active/

            - **PRD vision:** {prd?.Vision ?? "(missing)"} — {prd?.Goals.Length ?? 0} goal(s), {prd?.SuccessMetrics.Length ?? 0} success metric(s)
            - **SRS:** {srs?.FunctionalRequirements.Length ?? 0} functional + {srs?.QualityRequirements.Length ?? 0} quality requirement(s), {srs?.AcceptanceCriteria.Length ?? 0} acceptance criteria
            - **SDD:** {sdd?.Adrs.Length ?? 0} ADR(s), {sdd?.Controls.Length ?? 0} control(s)
            - **Readiness:** verdict '{readiness?.Verdict ?? "(missing)"}', {readiness?.Slices.Length ?? 0} slice(s)
            """;
    }

    private static string ApprovePrompt()
    {
        var bundleDigest = SpecificationStore.BundleDigest();

        return PromptFormatter.Format(
            input: $"""
            Review the bundle for this Specification run before publication (blueprint 0004 §5
            ApprovalEvaluator, §6 Publicação segura).

            {BundlePreview()}

            The current bundle digest is '{bundleDigest}'.

            Write a JSON OBJECT to the file '{ApprovalProposalPath}' (a real file, written with
            your file-write tool — NOT escaped or embedded inside the envelope you send back)
            with this shape: {ApprovalShape}
            `decision` must be exactly "approved" or "revise". `bundleDigest` must be set to
            exactly '{bundleDigest}' — it is re-checked against the CURRENT accepted chain at
            evaluation time, so if any accepted document changes after this preview, resend with
            the freshly reported digest instead of the one shown here. `rationale` must state a
            real reason, not a placeholder.

            If `decision` is "approved": the four accepted documents (PRD, SRS, SDD, readiness)
            are rendered and published to 'specs/active/' as 00-prd.md,
            10-software-requirements-specification.md, 20-software-design-document.md and
            30-readiness-handoff.md, and the run completes.

            If `decision` is "revise": the run routes back to the review phase so a fresh
            readiness verdict — including a FAIL:* one, if a deeper phase needs rework — can be
            issued.

            Return `approve` without arguments when done; the harness will validate the file and
            act on the decision, or re-request `approve` with the reported violations.
            """,
            output: new Envelope(EnvelopeType.Command, "approve", []));
    }

    private static string ApproveRetryPrompt(IEnumerable<string> violations)
    {
        var bundleDigest = SpecificationStore.BundleDigest();

        return PromptFormatter.Format(
            input: $"""
            The approval proposal at '{ApprovalProposalPath}' did not pass ApprovalEvaluator:
            {string.Join("\n", violations.Select(v => $"- {v}"))}

            The current bundle digest is '{bundleDigest}'. Rewrite the file at the exact same
            path with this shape: {ApprovalShape}
            `decision` must be exactly "approved" or "revise", `bundleDigest` must be set to
            exactly '{bundleDigest}', and `rationale` must state a real reason. Return `approve`
            without arguments for another harness-controlled attempt.
            """,
            output: new Envelope(EnvelopeType.Command, "approve", []));
    }
}
