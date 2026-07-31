using Harness.Engine;
namespace Flows.Development;

/// <summary>
/// Builds the development flow's prompts — the "strategy" kept separate from the state
/// machine in <c>DevelopmentTasks.cs</c>. Each step references its output token via a
/// constant (<c>$XXX</c>): the same name the driver fills in and returns as the next
/// envelope's arg.
/// </summary>
public static partial class DevelopmentTasks
{
    // Output tokens (the driver stores the step's artifact in these and returns them as args).
    private const string FEATURES = "$FEATURES";
    private const string VERIFY_CMD = "$VERIFY_CMD";
    private const string TARGET_DIR = "$TARGET_DIR";

    // feature_list's shape as a raw string with NO interpolation (literal braces) —
    // embedded in the prompts via {FeaturesShape} so it doesn't collide with $"""..."""
    // interpolation.
    private const string FeaturesShape =
        """[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[]}, ...]""";

    // Reinjects description/references (FeatureStore.Feature) into the implement prompt —
    // the only point of the loop that receives the whole Feature object, not just
    // title/id via StateStore. "" when the feature has neither (e.g. a feature_list.json
    // from a version before these fields existed) — the block disappears, it doesn't show
    // up empty.
    private static string FeatureContextBlock(Feature feature)
    {
        if (string.IsNullOrWhiteSpace(feature.Description) && feature.Refs.Length == 0)
            return "";

        var references = feature.Refs.Length > 0 ? string.Join(", ", feature.Refs) : "none";
        return $"""
            Description: {feature.Description}
            Brief references: {references}

            """;
    }

    // Reinjects the persisted brief (ArtifactStore, BriefArtifactName) at the point
    // of the loop that actually reasons about "what to build" — implement —
    // and only there: smoke/pick/verify/fix/handoff repair setup or do
    // bookkeeping, with no need for scope context. "" when the run started in interactive mode (no
    // docs/) or is a resume of a run from before this feature — in that case the block
    // disappears, it doesn't stay empty. Same treatment as the skills
    // (PromptFormatter.ReadSkills): line breaks become the literal "\n" marker and the
    // whole block ends up on a single line — the brief content doesn't need to preserve
    // its original Markdown formatting here, just be available. Always reinjecting the
    // SAME text, byte for byte, is also the lowest-cost bet to benefit from the driver's
    // underlying provider's prompt cache (not guaranteed: the harness only controls the
    // emitted text, not whether the driver marks a cache breakpoint there).
    private static string BriefBlock()
    {
        var brief = ArtifactStore.Read(BriefArtifactName);
        if (string.IsNullOrWhiteSpace(brief))
            return "";

        var singleLine = brief.Replace("\r\n", "\\n").Replace("\n", "\\n");
        return $"<brief>{singleLine}</brief>";
    }

    // --- session 0: initializer -----------------------------------------

    private static string InitializerPrompt(string content, string[] files) =>
        PromptFormatter.Format(
            input: $"""
            Initialize the development run from this brief by following the injected
            `dev-initializer` skill:

            <brief sources="{string.Join(", ", files)}">
            {content}
            </brief>

            Store a JSON ARRAY in '{FEATURES}': {FeaturesShape}
            (just the array, no passes — every feature is born pending). Store the verify
            command in '{VERIFY_CMD}' (e.g. `dotnet test`, `npm test`) and the target directory
            in '{TARGET_DIR}'. The `verify-feature.sh` may run the full suite at the start:
            `./init.sh`, then `$VERIFY_CMD`, print `PASS: feature <id> ...` and exit 0.
            """,
            output: new Envelope(EnvelopeType.Command, "plan", [FEATURES, VERIFY_CMD, TARGET_DIR]),
            skills: PromptFormatter.Skills("dev-initializer"));

    private static string InitializerInteractive() =>
        PromptFormatter.Format(
            input: $"""
            No brief was supplied. Ask the user for the goal, target directory, and established
            verification command, then follow the injected `dev-initializer` skill.

            Store a JSON ARRAY in '{FEATURES}' {FeaturesShape},
            the command in '{VERIFY_CMD}' and the directory in '{TARGET_DIR}'. The `verify-feature.sh`
            may run the full suite at the start: `./init.sh`, then `$VERIFY_CMD`, print
            `PASS: feature <id> ...` and exit 0.
            """,
            output: new Envelope(EnvelopeType.Command, "plan", [FEATURES, VERIFY_CMD, TARGET_DIR]),
            skills: PromptFormatter.Skills("dev-initializer"));

    private static string PlanRetryPrompt() =>
        PromptFormatter.Format(
            input: $"""
            Could not parse the feature list. Resend in '{FEATURES}' a valid JSON
            ARRAY, in exactly the format {FeaturesShape} — just the array, no surrounding text.
            Repeat the command `{VERIFY_CMD}` and `{TARGET_DIR}`.
            """,
            output: new Envelope(EnvelopeType.Command, "plan", [FEATURES, VERIFY_CMD, TARGET_DIR]));

    // --- per-feature loop (one fresh-context session) ------------------

    private static string ImplementPrompt(Feature feature) =>
        PromptFormatter.Format(
            input: $"""
            {ContextPolicy.NewFeaturePrefix()}
            Follow `dev-implement` for this feature:
            {BriefBlock()}
            {BearingsBlock()}
            Feature #{feature.Id} (priority {feature.Priority}): {feature.Title}
            {FeatureContextBlock(feature)}
            Target directory: {RunConfigStore.Load().TargetDir}

            Return `implement` without arguments when done. The harness derives the summary from Git.
            """,
            output: new Envelope(EnvelopeType.Command, "implement", []),
            skills: PromptFormatter.Skills("dev-implement"));

    private static string BearingsBlock()
    {
        var bearings = State("current_bearings");
        return string.IsNullOrWhiteSpace(bearings) ? "" : $"<bearings>{bearings}</bearings>";
    }

    private static string SmokeRetryPrompt(string failure) =>
        PromptFormatter.Format(
            input: $"""
            The deterministic smoke test failed: {failure}
            Repair the target setup using `dev-smoke`, then return `smoke` without arguments.
            The harness will rerun `init.sh` and decide from its exit code.
            """,
            output: new Envelope(EnvelopeType.Command, "smoke", []),
            skills: PromptFormatter.Skills("dev-smoke"));

    private static string VerifyPrompt() =>
        PromptFormatter.Format(
            input: $"""
            The harness could not execute a deterministic verification command for feature
            #{State(CurrentFeatureIdKey)} ({State(CurrentFeatureTitleKey)}). Repair it using
            `dev-verify`, then return `verify` without arguments. The harness reruns the verifier
            and decides from its process result.
            """,
            output: new Envelope(EnvelopeType.Command, "verify", []),
            skills: PromptFormatter.Skills("dev-verify"));

    private static string VerifyRetryPrompt() =>
        PromptFormatter.Format(
            input: $"""
            Deterministic verification is still unavailable. Repair it using `dev-verify`, then
            return `verify` without arguments for another harness-controlled attempt.
            """,
            output: new Envelope(EnvelopeType.Command, "verify", []),
            skills: PromptFormatter.Skills("dev-verify"));

    private static string FixPrompt(string? verifyFailure = null)
    {
        var failure = string.IsNullOrWhiteSpace(verifyFailure)
            ? ""
            : $"""
            Failure observed: {verifyFailure}

            """;

        return PromptFormatter.Format(
            input: $"""
            Verification FAILED on feature #{State(CurrentFeatureIdKey)}
            ({State(CurrentFeatureTitleKey)}). {failure}Follow `dev-implement` to fix only this
            feature. Return `implement` without arguments; the harness derives the new summary
            from Git.
            """,
            output: new Envelope(EnvelopeType.Command, "implement", []),
            skills: PromptFormatter.Skills("dev-implement"));
    }

    private static string HandoffPrompt(string? automaticFailure = null)
    {
        var failure = string.IsNullOrWhiteSpace(automaticFailure)
            ? ""
            : $"""
            Automatic handoff failed: {automaticFailure}

            """;

        return PromptFormatter.Format(
            input: $"""
            {failure}Repair the repository/progress state using `dev-handoff`, then return
            `handoff` without arguments. The harness will inspect the repository and retry the
            real handoff.
            """,
            output: new Envelope(EnvelopeType.Command, "handoff", []),
            skills: PromptFormatter.Skills("dev-handoff"));
    }

}
