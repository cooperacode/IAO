package main

import (
	"fmt"
	"strconv"
	"strings"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

// Output tokens (the driver stores the step's artifact in these and returns them as the
// next envelope's args).
const (
	tokenVerifyCmd = "$VERIFY_CMD"
	tokenTargetDir = "$TARGET_DIR"
)

// featuresShape is the feature_list shape embedded verbatim in the prompts.
const featuresShape = `[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[],"implementationContext":{"requirements":[],"constraints":[],"files":[],"acceptance":[]}}, ...]`

// featureContextBlock returns the current feature's bounded inline context for implement/fix.
func featureContextBlock(feature engine.Feature) string {
	if strings.TrimSpace(feature.Description) == "" && len(feature.References) == 0 && feature.ImplementationContext.IsEmpty() {
		return ""
	}

	references := "none"
	if len(feature.References) > 0 {
		references = strings.Join(feature.References, ", ")
	}
	implementationContext := ""
	if !feature.ImplementationContext.IsEmpty() {
		implementationContext = fmt.Sprintf("<implementation-context>%s</implementation-context>\n", feature.ImplementationContext.PromptText())
	}
	return fmt.Sprintf("Description: %s\nBrief references: %s\n%s\n", feature.Description, references, implementationContext)
}

func currentFeatureContextBlock() string {
	featureID, err := strconv.Atoi(state(currentFeatureIdKey))
	if err != nil {
		return ""
	}
	for _, feature := range engine.LoadFeatures() {
		if feature.Id == featureID {
			return featureContextBlock(feature)
		}
	}
	return ""
}

// --- session 0: initializer -----------------------------------------

func InitializerPrompt(content string, files []string) string {
	input := fmt.Sprintf(`Initialize the development run from this brief by following the injected
`+"`dev-initializer`"+` skill:

<brief sources="%s">
%s
</brief>

Write a JSON ARRAY to the file '%s' (a real file, written with your file-write tool —
NOT escaped or embedded inside the envelope you send back): %s
(just the array, no passes — every feature is born pending). Store the verify
command in '%s' (e.g. `+"`dotnet test`"+`, `+"`npm test`"+` — never a placeholder that`+`
always passes, like `+"`echo`"+`, `+"`true`"+`, or `+"`exit 0`"+`; it must run the project's
real build/test pipeline, creating one first if none exists yet) and the target directory
in '%s'. `+"`verify-feature.sh`"+` may run the full suite at the start:
`+"`./init.sh`"+`, then `+"`$VERIFY_CMD`"+`, print `+"`PASS: feature <id> ...`"+` and exit 0.
Plan one feature per distinct capability the brief describes — a single feature that
scaffolds everything is not a valid plan for a multi-requirement goal.`,
		strings.Join(files, ", "), content, planFilePath, featuresShape, tokenVerifyCmd, tokenTargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "plan", []string{tokenVerifyCmd, tokenTargetDir}),
		engine.Skills("dev-initializer"))
}

func InitializerInteractive() string {
	input := fmt.Sprintf(`No brief was supplied. Ask the user for the goal, target directory, and
established verification command, then follow the injected `+"`dev-initializer`"+` skill.

Write a JSON ARRAY to the file '%s' (a real file, written with your file-write tool —
NOT escaped or embedded inside the envelope you send back) %s,
the command in '%s' (never a placeholder that always passes, like `+"`echo`"+`, `+"`true`"+`,
or `+"`exit 0`"+`; it must run the project's real build/test pipeline) and the directory in
'%s'. `+"`verify-feature.sh`"+` may run the full suite at the start:
`+"`./init.sh`"+`, then `+"`$VERIFY_CMD`"+`, print `+"`PASS: feature <id> ...`"+` and exit 0.
Plan one feature per distinct capability the goal describes — a single feature that
scaffolds everything is not a valid plan for a multi-requirement goal.`,
		planFilePath, featuresShape, tokenVerifyCmd, tokenTargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "plan", []string{tokenVerifyCmd, tokenTargetDir}),
		engine.Skills("dev-initializer"))
}

func PlanRetryPrompt() string {
	// The retry instruction is short by design, but a driver that already dropped the
	// original (possibly large) brief from its context would otherwise have nothing left
	// to plan from and could fall back to inventing an unrelated feature. Reattaching the
	// persisted artifact (written once, in Start) keeps the retry grounded in the source.
	briefBlock := ""
	if brief := strings.TrimSpace(engine.ReadArtifact(briefArtifactName)); brief != "" {
		briefBlock = fmt.Sprintf("<brief>\n%s\n</brief>\n\n", brief)
	}

	input := fmt.Sprintf(`%sCould not read a valid JSON array from '%s'. Write the array itself to
that exact path with your file-write tool — do not put it in the envelope's args and do
not escape it as a string. Format: %s — just the array in the file, no surrounding text.
Repeat the command with '%s' and '%s'.`, briefBlock, planFilePath, featuresShape, tokenVerifyCmd, tokenTargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "plan", []string{tokenVerifyCmd, tokenTargetDir}),
		nil)
}

func SmokeFixPrompt(failure string) string {
	input := fmt.Sprintf("The deterministic smoke test failed: %s\nRepair the target setup using `dev-smoke`, then return `smoke` without arguments. The harness will rerun `init.sh` and decide from its exit code.", failure)
	return engine.Format(input, engine.NewEnvelope(engine.EnvelopeType.Command, "smoke", []string{}), engine.Skills("dev-smoke"))
}

func ImplementPrompt(feature engine.Feature) string {
	input := fmt.Sprintf("%s"+
		"Follow `dev-implement` for this feature:\nFeature #%d (priority %d): %s\n%sTarget directory: %s\n\n"+
		"Return `implement` without arguments when done. The harness derives the summary from Git.",
		engine.NewFeaturePrefix(), feature.Id, feature.Priority, feature.Title, featureContextBlock(feature),
		engine.LoadRunConfig().TargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{}),
		engine.Skills("dev-implement"))
}

func VerifyPrompt() string {
	config := engine.LoadRunConfig()
	input := fmt.Sprintf("The deterministic verifier could not be started for feature #%s (%s) in %s.\n"+
		"Repair it using `dev-verify`, then return `verify` without arguments. The harness reruns the verifier and decides from its process result.", state(currentFeatureIdKey), state(currentFeatureTitleKey), config.TargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "verify", []string{}),
		engine.Skills("dev-verify"))
}

func VerifyRetryPrompt() string {
	config := engine.LoadRunConfig()
	input := fmt.Sprintf("The deterministic verifier is unavailable for feature #%s in %s.\n"+
		"Repair it using `dev-verify`, then return `verify` without arguments for another harness-controlled attempt.", state(currentFeatureIdKey), config.TargetDir)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "verify", []string{}),
		engine.Skills("dev-verify"))
}

func FixPrompt(verifyFailure string) string {
	failure := ""
	if strings.TrimSpace(verifyFailure) != "" {
		failure = fmt.Sprintf("Failure observed: %s\n\n", verifyFailure)
	}

	input := fmt.Sprintf("Verification FAILED on feature #%s\n(%s).\n%s%sFollow `dev-implement` to fix only this feature.\n"+
		"Return `implement` without arguments; the harness derives the new summary from Git.",
		state(currentFeatureIdKey), state(currentFeatureTitleKey), currentFeatureContextBlock(), failure)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{}),
		engine.Skills("dev-implement"))
}

func HandoffPrompt(automaticFailure string) string {
	failure := ""
	if strings.TrimSpace(automaticFailure) != "" {
		failure = fmt.Sprintf("Automatic handoff failed: %s\n\n", automaticFailure)
	}

	input := fmt.Sprintf("%sRepair the repository/progress state using `dev-handoff`, then return `handoff` without arguments. The harness will inspect the repository and retry the real handoff.", failure)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "handoff", []string{}),
		engine.Skills("dev-handoff"))
}
