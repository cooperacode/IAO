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
    // and only there: bearings/smoke/pick/verify/fix/handoff just run a script or do
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
            You are the INITIALIZER (session 0). From the brief below:
            1. Ensure there is a Git repository in the target directory (run `git init` if needed) and create/reuse a dedicated working branch (never commit straight to main/master).
            2. Scaffold the target project's environment: create an idempotent `init.sh` that installs dependencies and brings up/builds the app, an idempotent `verify-feature.sh <id>` that verifies a feature, and the minimal folder structure.
            3. Expand the brief into a PRIORITIZED list of small, verifiable features, each independently implementable and testable. Number the priority (1 = highest). If a feature only makes sense after another one (e.g. it needs a schema another feature creates), record their ids in `dependsOn` — empty array when there is no dependency. The harness honors this order in addition to priority. Also fill in, for each feature: `description`, an objective description of what it does (up to {FeatureStore.DescriptionMaxChars} characters); and `references`, the explicit codes cited in the brief that relate to it (e.g. "RF-003", "JIRA-142", a named section) — empty array if the brief cites no explicit code for that feature (do not invent one).

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
            You are the INITIALIZER (session 0). Use the #tool:askQuestions and ask the user:
            (a) what to build (the app's goal), (b) the target directory, and (c) the verify
            command (e.g. `dotnet test`, `npm test`). Then:
            1. Ensure there is a Git repository in the target directory (run `git init` if needed) and create/reuse a dedicated working branch (never commit straight to main/master).
            2. Scaffold the environment: create an idempotent `init.sh` and an idempotent `verify-feature.sh <id>` in the target directory.
            3. Expand the goal into a PRIORITIZED list of small, verifiable features. If one depends on another, record their ids in `dependsOn` (empty array when there is none). Also fill in `description` (up to {FeatureStore.DescriptionMaxChars} characters) and `references` (explicit codes cited by the user for that feature; empty array if there are none).

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
            Implement EXCLUSIVELY this feature, incrementally and minimally — nothing beyond
            it:
            {BriefBlock()}
            {BearingsBlock()}
            Feature #{feature.Id} (priority {feature.Priority}): {feature.Title}
            {FeatureContextBlock(feature)}
            Work in the target directory ({RunConfigStore.Load().TargetDir}). If you run commands with
            long output, save it to `.harness/logs/` and do not paste logs into the response. When done,
            send the `implement` command again with no arguments. The harness derives the progress
            summary from the actual Git change set.
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
            Repair the target environment and resend the `smoke` command with no arguments. The
            harness will rerun `init.sh` and decide from its exit code.
            """,
            output: new Envelope(EnvelopeType.Command, "smoke", []),
            skills: PromptFormatter.Skills("dev-smoke"));

    private static string VerifyPrompt() =>
        PromptFormatter.Format(
            input: $"""
            The harness could not execute a deterministic verification command for feature
            #{State(CurrentFeatureIdKey)} ({State(CurrentFeatureTitleKey)}). Repair the configured
            target/verification setup, then resend `verify`; the harness will execute the command
            and decide from its exit code. Do not send a self-attested PASS.
            """,
            output: new Envelope(EnvelopeType.Command, "verify", []),
            skills: PromptFormatter.Skills("dev-verify"));

    private static string VerifyRetryPrompt() =>
        PromptFormatter.Format(
            input: $"""
            Deterministic verification is still unavailable. Repair the configured target or
            command and resend `verify` with no arguments. The harness will rerun it and inspect
            the exit code; a textual PASS is not accepted as evidence.
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
            ({State(CurrentFeatureTitleKey)}). {failure}Fix the implementation (still ONLY this feature).
            If you check logs, read only the relevant excerpt. When fixed, send the `implement`
            command again with no arguments; the harness will verify again next.
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
            {failure}Leave the state CLEAN for the next session:
            1. `git commit` with a descriptive message referencing feature #{State(CurrentFeatureIdKey)}. If the target directory is not a Git repository, record this explicitly as `NO_GIT: <reason>`.
            2. Append a line to `progress.txt` in this exact format (same as the automatic handoff, so entries stay consistent): `[YYYY-MM-DD HH:MM UTC] Feature #<id> - <title>: <what was done>. Verify with: <command>. Result: <result>`.

            After the repair, send the `handoff` command. The harness will inspect the actual Git
            state and progress file; a textual commit hash is not accepted as evidence.
            """,
            output: new Envelope(EnvelopeType.Command, "handoff", []),
            skills: PromptFormatter.Skills("dev-handoff"));
    }

}
