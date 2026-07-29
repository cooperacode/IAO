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
	input := fmt.Sprintf(`=== NEW SESSION (clean context) ===
You are a coding agent starting a FRESH session. Do not assume anything from the
previous session — all state lives in the persistent artifacts.
%s
Get your bearings with short output: run `+"`pwd`"+`, read only the tail of `+"`progress.txt`"+` and the
recent `+"`git log --oneline`"+` to understand what has already been done. Do not paste long
logs; if you need to preserve detail, save it in `+"`.harness/logs/`"+`.

Summarize what you found in '%s' in 2-4 lines.`, briefBlock(), tokenNote)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "bearings", []string{tokenNote}),
		engine.Skills("dev-bearings"))
}

func SmokePrompt() string {
	input := fmt.Sprintf("Smoke test: run `./init.sh` in the target directory (%s) and confirm\n"+
		"the baseline comes up/builds without error before touching any feature. Save the\n"+
		"full output to `.harness/logs/smoke.log` and report in '%s' just `ok` or the\n"+
		"main error and the log path.", engine.LoadRunConfig().TargetDir, tokenSmoke)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "smoke", []string{tokenSmoke}),
		engine.Skills("dev-smoke"))
}

func PickPrompt() string {
	input := "Baseline confirmed. Send the `pick` command to receive the next feature to\n" +
		"implement (the highest-priority one still pending — the harness chooses)."

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "pick", []string{}),
		nil)
}

func ImplementPrompt(feature engine.Feature) string {
	input := fmt.Sprintf("Implement EXCLUSIVELY this feature, incrementally and minimally — nothing beyond\n"+
		"it:\n%s\nFeature #%d (priority %d): %s\n%sWork in the target directory (%s). If you run commands with\n"+
		"long output, save it to `.harness/logs/` and do not paste logs into the summary. When done,\n"+
		"summarize what you implemented in '%s' in one short sentence.",
		briefBlock(), feature.Id, feature.Priority, feature.Title, featureContextBlock(feature),
		engine.LoadRunConfig().TargetDir, tokenSummary)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{tokenSummary}),
		engine.Skills("dev-implement"))
}

func VerifyPrompt() string {
	config := engine.LoadRunConfig()
	input := fmt.Sprintf("The harness did not find `verify-feature.sh` in the target directory, so do a\n"+
		"manual self-verify of feature #%s\n(%s) the way a user would: run\n"+
		"`%s` in the target directory (%s) and\nconfirm the behavior end to end. Save the full output to\n"+
		"`.harness/logs/verify-%s.log`.\n\nRespond in '%s' starting with `PASS` or `FAIL: <reason>`, including only the\n"+
		"main error and the log path.",
		state(currentFeatureIdKey), state(currentFeatureTitleKey), config.VerifyCmd, config.TargetDir,
		state(currentFeatureIdKey), tokenResult)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "verify", []string{tokenResult}),
		engine.Skills("dev-verify"))
}

func VerifyRetryPrompt() string {
	config := engine.LoadRunConfig()
	input := fmt.Sprintf("The self-verify verdict did not start with `PASS` or `FAIL`. Re-run, if\n"+
		"needed, `%s` in the target directory (%s)\nsaving the full output to `.harness/logs/verify-%s.log`.\n"+
		"Respond in '%s' starting exactly with `PASS` or `FAIL: <reason>`,\nwithout pasting long logs.",
		config.VerifyCmd, config.TargetDir, state(currentFeatureIdKey), tokenResult)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "verify", []string{tokenResult}),
		engine.Skills("dev-verify"))
}

func FixPrompt(verifyFailure string) string {
	failure := ""
	if strings.TrimSpace(verifyFailure) != "" {
		failure = fmt.Sprintf("Failure observed: %s\n\n", verifyFailure)
	}

	input := fmt.Sprintf("Verification FAILED on feature #%s\n(%s). %sFix the implementation (still ONLY this feature).\n"+
		"If you check logs, read only the relevant excerpt. Summarize the fix in '%s' —\nwe'll verify again next.",
		state(currentFeatureIdKey), state(currentFeatureTitleKey), failure, tokenSummary)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "implement", []string{tokenSummary}),
		engine.Skills("dev-implement"))
}

func HandoffPrompt(automaticFailure string) string {
	failure := ""
	if strings.TrimSpace(automaticFailure) != "" {
		failure = fmt.Sprintf("Automatic handoff failed: %s\n\n", automaticFailure)
	}

	input := fmt.Sprintf("%sLeave the state CLEAN for the next session:\n"+
		"1. `git commit` with a descriptive message referencing feature #%s. If the target directory is not a Git repository, record this explicitly as `NO_GIT: <reason>`.\n"+
		"2. Append a line to `progress.txt` in this exact format (same as the automatic handoff, so entries stay consistent): `[YYYY-MM-DD HH:MM UTC] Feature #<id> - <title>: <what was done>. Verify with: <command>. Result: <result>`.\n\n"+
		"Confirm with the commit hash or `NO_GIT: <reason>` in '%s'.",
		failure, state(currentFeatureIdKey), tokenCommit)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "handoff", []string{tokenCommit}),
		engine.Skills("dev-handoff"))
}

func HandoffRetryPrompt() string {
	input := fmt.Sprintf("The handoff confirmation came back empty. Update `progress.txt` in the target directory\n"+
		"(%s) and respond in '%s' with the commit hash or\n`NO_GIT: <reason>` when there is no Git repository.",
		engine.LoadRunConfig().TargetDir, tokenCommit)

	return engine.Format(input,
		engine.NewEnvelope(engine.EnvelopeType.Command, "handoff", []string{tokenCommit}),
		engine.Skills("dev-handoff"))
}
