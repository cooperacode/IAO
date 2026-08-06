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
    private const string VERIFY_CMD = "$VERIFY_CMD";
    private const string TARGET_DIR = "$TARGET_DIR";

    // feature_list's shape as a raw string with NO interpolation (literal braces) —
    // embedded in the prompts via {FeaturesShape} so it doesn't collide with $"""..."""
    // interpolation.
    private const string FeaturesShape =
        """[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[],"implementationContext":{"requirements":[],"constraints":[],"files":[],"acceptance":[]}}, ...]""";

    // Reinjects the current feature's bounded inline context into implement/fix prompts.
    private static string FeatureContextBlock(Feature feature)
    {
        if (string.IsNullOrWhiteSpace(feature.Description)
            && feature.Refs.Length == 0
            && feature.Context.IsEmpty)
            return "";

        var references = feature.Refs.Length > 0 ? string.Join(", ", feature.Refs) : "none";
        var implementationContext = feature.Context.IsEmpty
            ? ""
            : $"<implementation-context>{feature.Context.ToPromptText()}</implementation-context>";
        return $"""
            Description: {feature.Description}
            Brief references: {references}
            {implementationContext}

            """;
    }

    // --- session 0: initializer -----------------------------------------

    private static string InitializerPrompt(string content, string[] files) =>
        PromptFormatter.Format(
            input: $"""
            Initialize the development run from this brief by following the injected
            `dev-initializer` skill:

            <brief sources="{string.Join(", ", files)}">{PromptFormatter.Inline(content)}</brief>

            Write a JSON ARRAY to the file '{PlanFilePath}' (a real file, written with your
            file-write tool — NOT escaped or embedded inside the envelope you send back): {FeaturesShape}
            (just the array, no passes — every feature is born pending). Store the verify
            command in '{VERIFY_CMD}' (e.g. `dotnet test`, `npm test` — never a placeholder that
            always passes, like `echo`, `true`, or `exit 0`; it must run the project's real
            build/test pipeline, creating one first if none exists yet) and the target directory
            in '{TARGET_DIR}'. The `verify-feature.sh` may run the full suite at the start:
            `./init.sh`, then `$VERIFY_CMD`, print `PASS: feature <id> ...` and exit 0.
            Plan one feature per distinct capability the brief describes — a single feature that
            scaffolds everything is not a valid plan for a multi-requirement goal.
            """,
            output: new Envelope(EnvelopeType.Command, "plan", [VERIFY_CMD, TARGET_DIR]),
            skills: PromptFormatter.Skills("dev-initializer"));

    private static string InitializerInteractive() =>
        PromptFormatter.Format(
            input: $"""
            No brief was supplied. Ask the user for the goal, target directory, and established
            verification command, then follow the injected `dev-initializer` skill.

            Write a JSON ARRAY to the file '{PlanFilePath}' (a real file, written with your
            file-write tool — NOT escaped or embedded inside the envelope you send back) {FeaturesShape},
            the command in '{VERIFY_CMD}' (never a placeholder that always passes, like `echo`,
            `true`, or `exit 0`; it must run the project's real build/test pipeline) and the
            directory in '{TARGET_DIR}'. The `verify-feature.sh` may run the full suite at the
            start: `./init.sh`, then `$VERIFY_CMD`, print `PASS: feature <id> ...` and exit 0.
            Plan one feature per distinct capability the goal describes — a single feature that
            scaffolds everything is not a valid plan for a multi-requirement goal.
            """,
            output: new Envelope(EnvelopeType.Command, "plan", [VERIFY_CMD, TARGET_DIR]),
            skills: PromptFormatter.Skills("dev-initializer"));

    private static string PlanRetryPrompt()
    {
        // The retry instruction is short by design, but a driver that already dropped the
        // original (possibly large) brief from its context would otherwise have nothing left
        // to plan from and could fall back to inventing an unrelated feature. Reattaching the
        // persisted artifact (written once, in Start) keeps the retry grounded in the source.
        var brief = ArtifactStore.Read(BriefArtifactName).Trim();
        var briefBlock = brief.Length == 0 ? "" : $"""
            <brief>
            {brief}
            </brief>


            """;

        return PromptFormatter.Format(
            input: $"""
            {briefBlock}Could not read a valid JSON array from '{PlanFilePath}'. Write the array
            itself to that exact path with your file-write tool — do not put it in the envelope's
            args and do not escape it as a string. Format: {FeaturesShape} — just the array in the
            file, no surrounding text. Repeat the command with `{VERIFY_CMD}` and `{TARGET_DIR}`.
            """,
            output: new Envelope(EnvelopeType.Command, "plan", [VERIFY_CMD, TARGET_DIR]));
    }

    // --- per-feature loop (one fresh-context session) ------------------

    private static string ImplementPrompt(Feature feature) =>
        PromptFormatter.Format(
            input: $"""
            {ContextPolicy.NewFeaturePrefix()}
            Follow `dev-implement` for this feature:
            Feature #{feature.Id} (priority {feature.Priority}): {feature.Title}
            {FeatureContextBlock(feature)}
            Target directory: {RunConfigStore.Load().TargetDir}

            Return `implement` without arguments when done. The harness derives the summary from Git.
            """,
            output: new Envelope(EnvelopeType.Command, "implement", []),
            skills: PromptFormatter.Skills("dev-implement"));

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
        var featureContext = int.TryParse(State(CurrentFeatureIdKey), out var featureId)
            ? FeatureStore.Load().FirstOrDefault(f => f.Id == featureId) is { } feature
                ? FeatureContextBlock(feature)
                : ""
            : "";
        var failure = string.IsNullOrWhiteSpace(verifyFailure)
            ? ""
            : $"""
            Failure observed: {verifyFailure}

            """;

        return PromptFormatter.Format(
            input: $"""
            Verification FAILED on feature #{State(CurrentFeatureIdKey)}
            ({State(CurrentFeatureTitleKey)}).
            {featureContext}{failure}Follow `dev-implement` to fix only this
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
