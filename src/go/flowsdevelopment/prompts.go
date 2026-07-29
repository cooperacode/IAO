package main

import (
	"fmt"
	"strings"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

// Output tokens (the driver stores the step's artifact in these and returns them as the
// next envelope's args).
const (
	tokenFeatures  = "$FEATURES"
	tokenVerifyCmd = "$VERIFY_CMD"
	tokenTargetDir = "$TARGET_DIR"
	tokenNote      = "$NOTE"
	tokenSmoke     = "$SMOKE"
	tokenSummary   = "$SUMMARY"
	tokenResult    = "$RESULT"
	tokenCommit    = "$COMMIT"
)

// featuresShape is the feature_list shape embedded verbatim in the prompts.
const featuresShape = `[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[]}, ...]`

// featureContextBlock reinjects description/references (engine.Feature) into the implement
// prompt — the only point of the loop that receives the whole Feature object, not just
// title/id via StateStore. "" when the feature has neither (e.g. a feature_list.json from a
// version before these fields existed) — the block disappears, it doesn't show up empty.
func featureContextBlock(feature engine.Feature) string {
	if strings.TrimSpace(feature.Description) == "" && len(feature.References) == 0 {
		return ""
	}

	references := "none"
	if len(feature.References) > 0 {
		references = strings.Join(feature.References, ", ")
	}
	return fmt.Sprintf("Description: %s\nBrief references: %s\n\n", feature.Description, references)
}

// briefBlock reinjects the persisted brief (ArtifactStore, briefArtifactName) at the two
// points of the loop that actually reason about "what to build" — bearings and implement,
// and only there: smoke/pick/verify/fix/handoff just run a script or do bookkeeping, with
// no need for scope context. "" when the run started in interactive mode (no docs/) or is
// resuming a run from before this feature — in that case the block disappears, it doesn't
// stay empty.
func briefBlock() string {
	brief := engine.ReadArtifact(briefArtifactName)
	if strings.TrimSpace(brief) == "" {
		return ""
	}

	singleLine := strings.ReplaceAll(brief, "\r\n", "\\n")
	singleLine = strings.ReplaceAll(singleLine, "\n", "\\n")
	return fmt.Sprintf("<brief>%s</brief>", singleLine)
}

// --- session 0: initializer -----------------------------------------

func InitializerPrompt(content string, files []string) string {
	input := fmt.Sprintf(`You are the INITIALIZER (session 0). From the brief below:
1. Ensure there is a Git repository in the target directory (run `+"`git init`"+` if needed) and create/reuse a dedicated working branch (never commit straight to main/master).
2. Scaffold the target project's environment: create an idempotent `+"`init.sh`"+` that installs dependencies and brings up/builds the app, an idempotent `+"`verify-feature.sh <id>`"+` that verifies a feature, and the minimal folder structure.
3. Expand the brief into a PRIORITIZED list of small, verifiable features, each independently implementable and testable. Number the priority (1 = highest). If a feature only makes sense after another one (e.g. it needs a schema another feature creates), record their ids in `+"`dependsOn`"+` — empty array when there is no dependency. The harness honors this order in addition to priority. Also fill in, for each feature: `+"`description`"+`, an objective description of what it does (up to %d characters); and `+"`references`"+`, the explicit codes cited in the brief that relate to it (e.g. "RF-003", "JIRA-142", a named section) — empty array if the brief cites no explicit code for that feature (do not invent one).

<brief sources="%s">
%s
</brief>

Store a JSON ARRAY in '%s': %s
(just the array, no passes — every feature is born pending). Store the verify
command in '%s' (e.g. `+"`dotnet test`"+`, `+"`npm test`"+`) and the target directory
in '%s'. `+"`verify-feature.sh`"+` may run the full suite at the start:
`+"`./init.sh`"+`, then `+"`$VERIFY_CMD`"+`, print `+"`PASS: feature <id> ...`"+` and exit 0.`,
		engine.DescriptionMaxChars, strings.Join(files, ", "), content, tokenFeatures, featuresShape, tokenVerifyCmd, tokenTargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "plan", []string{tokenFeatures, tokenVerifyCmd, tokenTargetDir}),
		engine.Skills("dev-initializer"))
}

func InitializerInteractive() string {
	input := fmt.Sprintf(`You are the INITIALIZER (session 0). Use the #tool:askQuestions and ask the user:
(a) what to build (the app's goal), (b) the target directory, and (c) the verify
command (e.g. `+"`dotnet test`"+`, `+"`npm test`"+`). Then:
1. Ensure there is a Git repository in the target directory (run `+"`git init`"+` if needed) and create/reuse a dedicated working branch (never commit straight to main/master).
2. Scaffold the environment: create an idempotent `+"`init.sh`"+` and an idempotent `+"`verify-feature.sh <id>`"+` in the target directory.
3. Expand the goal into a PRIORITIZED list of small, verifiable features. If one depends on another, record their ids in `+"`dependsOn`"+` (empty array when there is none). Also fill in `+"`description`"+` (up to %d characters) and `+"`references`"+` (explicit codes cited by the user for that feature; empty array if there are none).

Store a JSON ARRAY in '%s' %s,
the command in '%s' and the directory in '%s'. `+"`verify-feature.sh`"+` may run the full suite at the start:
`+"`./init.sh`"+`, then `+"`$VERIFY_CMD`"+`, print `+"`PASS: feature <id> ...`"+` and exit 0.`,
		engine.DescriptionMaxChars, tokenFeatures, featuresShape, tokenVerifyCmd, tokenTargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "plan", []string{tokenFeatures, tokenVerifyCmd, tokenTargetDir}),
		engine.Skills("dev-initializer"))
}

func PlanRetryPrompt() string {
	input := fmt.Sprintf(`Could not parse the feature list. Resend in '%s' a valid JSON
ARRAY, in exactly the format %s — just the array, no surrounding text.
Repeat the command '%s' and '%s'.`, tokenFeatures, featuresShape, tokenVerifyCmd, tokenTargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "plan", []string{tokenFeatures, tokenVerifyCmd, tokenTargetDir}),
		nil)
}

// --- per-feature loop (one fresh-context session) ------------------

func BearingsPrompt() string {
	input := "=== NEW SESSION (clean context) ===\n" + briefBlock() +
		"The harness already captured bounded bearings (working directory, progress tail, and recent git history).\n" +
		"Continue with the smoke step; do not return a bearings note."

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "bearings", []string{}),
		engine.Skills("dev-bearings"))
}

func SmokePrompt() string {
	input := fmt.Sprintf("The harness runs `./init.sh` deterministically in the target directory (%s).\n"+
		"It stores full output in `.harness/logs/smoke.log`; proceed without a smoke verdict.", engine.LoadRunConfig().TargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "smoke", []string{}),
		engine.Skills("dev-smoke"))
}

func PickPrompt() string {
	input := "Baseline confirmed. Send the `pick` command to receive the next feature to\n" +
		"implement (the highest-priority one still pending — the harness chooses)."

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "pick", []string{}),
		nil)
}

func SmokeFixPrompt(failure string) string {
	input := fmt.Sprintf("Smoke failed deterministically: %s\nFix the target setup, then return `smoke` without arguments.", failure)
	return engine.Format(input, engine.NewEnvelope(engine.EnvelopeType.Command, "smoke", []string{}), engine.Skills("dev-smoke"))
}

func ImplementPrompt(feature engine.Feature) string {
	input := fmt.Sprintf("Implement EXCLUSIVELY this feature, incrementally and minimally — nothing beyond\n"+
		"it:\n%s\nFeature #%d (priority %d): %s\n%sWork in the target directory (%s). If you run commands with\n"+
		"long output, save it to `.harness/logs/`. Return `implement` without arguments when done;\n"+
		"the harness derives the summary from the actual Git diff.",
		briefBlock(), feature.Id, feature.Priority, feature.Title, featureContextBlock(feature),
		engine.LoadRunConfig().TargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{}),
		engine.Skills("dev-implement"))
}

func VerifyPrompt() string {
	config := engine.LoadRunConfig()
	input := fmt.Sprintf("The deterministic verifier could not be started for feature #%s (%s) in %s.\n"+
		"Repair the verification setup and return `implement` without arguments.", state(currentFeatureIdKey), state(currentFeatureTitleKey), config.TargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{}),
		engine.Skills("dev-verify"))
}

func VerifyRetryPrompt() string {
	config := engine.LoadRunConfig()
	input := fmt.Sprintf("The deterministic verifier is unavailable for feature #%s in %s.\n"+
		"Repair or create it and return `implement` without arguments.", state(currentFeatureIdKey), config.TargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{}),
		engine.Skills("dev-verify"))
}

func FixPrompt(verifyFailure string) string {
	failure := ""
	if strings.TrimSpace(verifyFailure) != "" {
		failure = fmt.Sprintf("Failure observed: %s\n\n", verifyFailure)
	}

	input := fmt.Sprintf("Verification FAILED on feature #%s\n(%s). %sFix the implementation (still ONLY this feature).\n"+
		"If you check logs, read only the relevant excerpt. Return `implement` without arguments; the harness derives the new summary from Git.",
		state(currentFeatureIdKey), state(currentFeatureTitleKey), failure)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{}),
		engine.Skills("dev-implement"))
}

func HandoffPrompt(automaticFailure string) string {
	failure := ""
	if strings.TrimSpace(automaticFailure) != "" {
		failure = fmt.Sprintf("Automatic handoff failed: %s\n\n", automaticFailure)
	}

	input := fmt.Sprintf("%sAutomatic handoff requires a deterministic PASS. Return `handoff` without arguments so the harness can retry the real progress/git operation.", failure)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "handoff", []string{}),
		engine.Skills("dev-handoff"))
}

func HandoffRetryPrompt() string {
	input := "The handoff is deterministic and only runs after a recorded PASS. Return `handoff` without arguments after verification passes."

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "handoff", []string{}),
		engine.Skills("dev-handoff"))
}
