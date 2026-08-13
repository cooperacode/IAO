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

    private const string IdeaShape =
        """{"schema":"iao/idea/v1","title":"...","problem":"...","users":["..."],"desiredOutcomes":["..."],"constraints":["..."],"openQuestions":[{"id":"OQ-1","question":"...","blocking":false}]}""";

    private const string PrdShape =
        """{"schema":"iao/prd/v1","ideaDigest":"sha256:...","vision":"...","goals":[{"id":"G-1","statement":"..."}],"successMetrics":[{"id":"M-1","goalId":"G-1","measure":"...","target":"..."}],"nonGoals":["..."],"scope":["..."],"risks":[{"id":"R-1","description":"...","mitigation":"...","severity":"..."}],"decisions":[{"id":"D-1","statement":"...","rationale":"..."}],"openQuestions":[]}""";

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
            either persist prd.accepted.json and stop, or re-request `product` with the reported
            violations.
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
}
