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
    private const string NOTE = "$NOTE";
    private const string SMOKE = "$SMOKE";
    private const string SUMMARY = "$SUMMARY";
    private const string RESULT = "$RESULT";
    private const string COMMIT = "$COMMIT";

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

    // Reinjects the persisted brief (ArtifactStore, BriefArtifactName) at the two points
    // of the loop that actually reason about "what to build" — bearings and implement —
    // and only there: smoke/pick/verify/fix/handoff just run a script or do bookkeeping,
    // with no need for scope context. "" when the run started in interactive mode (no
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

    private static string BearingsPrompt() =>
        PromptFormatter.Format(
            input: $"""
            === NEW SESSION (clean context) ===
            You are a coding agent starting a FRESH session. Do not assume anything from the
            previous session — all state lives in the persistent artifacts.
            {BriefBlock()}
            Get your bearings with short output: run `pwd`, read only the tail of `progress.txt` and the
            recent `git log --oneline` to understand what has already been done. Do not paste long
            logs; if you need to preserve detail, save it in `.harness/logs/`.

            Summarize what you found in '{NOTE}' in 2-4 lines.
            """,
            output: new Envelope(EnvelopeType.Command, "bearings", [NOTE]),
            skills: PromptFormatter.Skills("dev-bearings"));

    private static string SmokePrompt() =>
        PromptFormatter.Format(
            input: $"""
            Smoke test: run `./init.sh` in the target directory ({RunConfigStore.Load().TargetDir}) and confirm
            that the baseline comes up/builds without error before touching any feature. Save the
            full output to `.harness/logs/smoke.log` and report in '{SMOKE}' just `ok` or the
            main error and the log path.
            """,
            output: new Envelope(EnvelopeType.Command, "smoke", [SMOKE]),
            skills: PromptFormatter.Skills("dev-smoke"));

    private static string PickPrompt() =>
        PromptFormatter.Format(
            input: """
            Baseline confirmed. Send the `pick` command to receive the next feature to
            implement (the highest-priority one still pending — the harness chooses).
            """,
            output: new Envelope(EnvelopeType.Command, "pick", []));

    private static string ImplementPrompt(Feature feature) =>
        PromptFormatter.Format(
            input: $"""
            Implement EXCLUSIVELY this feature, incrementally and minimally — nothing beyond
            it:
            {BriefBlock()}
            Feature #{feature.Id} (priority {feature.Priority}): {feature.Title}
            {FeatureContextBlock(feature)}
            Work in the target directory ({RunConfigStore.Load().TargetDir}). If you run commands with
            long output, save it to `.harness/logs/` and do not paste logs into the summary. When done,
            summarize what you implemented in '{SUMMARY}' in one short sentence.
            """,
            output: new Envelope(EnvelopeType.Command, "implement", [SUMMARY]),
            skills: PromptFormatter.Skills("dev-implement"));

    private static string VerifyPrompt() =>
        PromptFormatter.Format(
            input: $"""
            The harness did not find `verify-feature.sh` in the target directory, so do a
            manual self-verify of feature #{State(CurrentFeatureIdKey)}
            ({State(CurrentFeatureTitleKey)}) the way a user would: run
            `{RunConfigStore.Load().VerifyCmd}` in the target directory ({RunConfigStore.Load().TargetDir}) and
            confirm the behavior end to end. Save the full output to
            `.harness/logs/verify-{State(CurrentFeatureIdKey)}.log`.

            Respond in '{RESULT}' starting with `PASS` or `FAIL: <reason>`, including only the
            main error and the log path.
            """,
            output: new Envelope(EnvelopeType.Command, "verify", [RESULT]),
            skills: PromptFormatter.Skills("dev-verify"));

    private static string VerifyRetryPrompt() =>
        PromptFormatter.Format(
            input: $"""
            The self-verify verdict did not start with `PASS` or `FAIL`. Re-run, if
            needed, `{RunConfigStore.Load().VerifyCmd}` in the target directory ({RunConfigStore.Load().TargetDir})
            saving the full output to `.harness/logs/verify-{State(CurrentFeatureIdKey)}.log`.
            Respond in '{RESULT}' starting exactly with `PASS` or `FAIL: <reason>`,
            without pasting long logs.
            """,
            output: new Envelope(EnvelopeType.Command, "verify", [RESULT]),
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
            If you check logs, read only the relevant excerpt. Summarize the fix in '{SUMMARY}' —
            we'll verify again next.
            """,
            output: new Envelope(EnvelopeType.Command, "implement", [SUMMARY]),
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

            Confirm with the commit hash or `NO_GIT: <reason>` in '{COMMIT}'.
            """,
            output: new Envelope(EnvelopeType.Command, "handoff", [COMMIT]),
            skills: PromptFormatter.Skills("dev-handoff"));
    }

    private static string HandoffRetryPrompt() =>
        PromptFormatter.Format(
            input: $"""
            The handoff confirmation came back empty. Update `progress.txt` in the target directory
            ({RunConfigStore.Load().TargetDir}) and respond in '{COMMIT}' with the commit hash or
            `NO_GIT: <reason>` when there is no Git repository.
            """,
            output: new Envelope(EnvelopeType.Command, "handoff", [COMMIT]),
            skills: PromptFormatter.Skills("dev-handoff"));
}
