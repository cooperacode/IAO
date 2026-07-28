// Package main implements the long-running development flow (the "Effective harnesses for
// long-running agents" pattern, Anthropic) — the Go port of Flows.Development (.NET),
// flows_development (Python/Rust). An initializer (session 0) expands the brief into a
// prioritized feature list; then a loop of fresh-context sessions implements ONE feature at
// a time:
//
//	start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
//
// State that survives hard resets lives in persistent artifacts: the feature store
// (feature_list.json, from the harness) and progress.txt + git (from the target
// directory). Each task only performs effects and decides the NEXT command (the output
// Envelope) — orchestration (dispatch, global guards, transport) lives in harnessengine.
package main

import (
	"fmt"
	"os"
	"strconv"
	"strings"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

const (
	// MaxFeatures/StepsPerFeature are this flow's local guards (the global harness.json
	// ceiling, 12, is too short for a loop). Few features + a PER-FEATURE step ceiling bars
	// an implement<->verify loop that never closes.
	MaxFeatures     = 10
	StepsPerFeature = 8
	// StepBudget is the effective step ceiling passed to harnessengine.Run (override of the
	// global one): slack for the worst case of MaxFeatures features spending StepsPerFeature
	// each, plus start/plan and the boundaries.
	StepBudget = MaxFeatures*StepsPerFeature + 8
)

// State keys used by this flow's task functions (tasks.go/prompts.go/verify.go/handoff.go).
const (
	currentFeatureIdKey      = "current_feature_id"
	currentFeatureTitleKey   = "current_feature_title"
	currentFeatureSummaryKey = "current_feature_summary"
	currentFeatureVerifyKey  = "current_feature_verify"
	featureStepsKey          = "feature_steps"

	// briefArtifactName is the brief artifact's name in the ArtifactStore (.harness/brief.md)
	// — persisted in Start() so it can be reinjected into bearings/implement (prompts.go).
	briefArtifactName = "brief"
)

func state(key string) string {
	if v := engine.GetState(key); v != nil {
		return *v
	}
	return ""
}

func docsFolder() string {
	return engine.CurrentConfig().DocsFolder
}

// Start begins or resumes the flow.
func Start() string {
	// A previous session (perhaps from another driver — tokens ran out in one IDE and
	// another takes over) may have died mid-feature. Restarting would discard work in
	// progress; resuming is safe and deterministic: Bearings is reentrant by construction
	// (it only rearms the per-feature guard) and the next Pick() reselects the same,
	// still-pending feature — without needing to know exactly where the previous session
	// stopped.
	if engine.PendingFeatureCount() > 0 {
		fmt.Fprintln(os.Stderr,
			"[dev] run em andamento detectado (feature pendente); retomando via bearings em vez de resetar.")
		return BearingsPrompt()
	}

	// PRODUCER flow of the feature list: a new run discards the previous one's.
	engine.ResetFeatures()
	engine.ResetRunConfig()
	// Without this, a new run in interactive mode (no docs/) would silently inherit a
	// previous run's brief.md — interactive mode never calls WriteArtifact, so only this
	// reset guarantees no brief from an old topic survives.
	engine.ResetArtifacts()

	// The brief (what to build) comes from docs/, or, without docs, from interactive mode.
	if !engine.HasDocs(docsFolder()) {
		return InitializerInteractive()
	}

	content, files := engine.ReadDocs(docsFolder())
	// Persisted to be reinjected into bearings/implement (prompts.go) — before this
	// feature, "content" was just a local variable of this turn, discarded as soon as the
	// initializer finished.
	engine.WriteArtifact(briefArtifactName, content)
	engine.SetState("origem", "docs")
	return InitializerPrompt(content, files)
}

// Plan interprets the driver's feature array and persists the run configuration.
func Plan(envelope *engine.Envelope) string {
	features := engine.ParseFeatures(arg(envelope))
	if len(features) == 0 {
		return PlanRetryPrompt() // didn't parse → re-request (corrective loop)
	}

	// Feature ceiling: keeps the highest-priority ones (lowest number).
	capped := capFeatures(features, MaxFeatures)

	// Sanitizes DependsOn: a surviving feature may depend on an id cut above, which would
	// block it forever (never "ready") with no way for the driver to know — the harness
	// did the cutting, not it. Trimming nodes from an already-acyclic graph (validated in
	// ParseFeatures) cannot create a cycle, so only cleaning dangling refs is necessary.
	cappedIds := make(map[int]bool, len(capped))
	for _, f := range capped {
		cappedIds[f.Id] = true
	}
	for i := range capped {
		filtered := make([]int, 0, len(capped[i].DependsOn))
		for _, dep := range capped[i].DependsOn {
			if cappedIds[dep] {
				filtered = append(filtered, dep)
			}
		}
		capped[i].DependsOn = filtered
	}

	engine.WriteFeatures(capped)

	// Verification command, target dir, and run identity: rehydrated on every smoke/verify
	// step. Outside state.json on purpose - see RunConfigStore. RunId is born here (the
	// same moment Start() decided this is a new, not resumed, run) and survives every
	// following session without needing to appear in the Envelope exchanged with the model
	// (RFC §6.4 — run identity is a control-plane concern, not the contract's).
	engine.WriteRunConfig(engine.RunConfig{
		VerifyCmd: argAt(envelope, 1, "dotnet test"),
		TargetDir: argAt(envelope, 2, "."),
		RunId:     newRunId(),
	})

	return BearingsPrompt()
}

func capFeatures(features []engine.Feature, max int) []engine.Feature {
	sorted := make([]engine.Feature, len(features))
	copy(sorted, features)
	sortFeatures(sorted)
	if len(sorted) > max {
		sorted = sorted[:max]
	}
	return sorted
}

func sortFeatures(features []engine.Feature) {
	// Stable insertion sort by (Priority, Id) — small, bounded lists (driver-returned
	// feature arrays), clarity over asymptotic elegance.
	for i := 1; i < len(features); i++ {
		j := i
		for j > 0 && less(features[j], features[j-1]) {
			features[j], features[j-1] = features[j-1], features[j]
			j--
		}
	}
}

func less(a, b engine.Feature) bool {
	if a.Priority != b.Priority {
		return a.Priority < b.Priority
	}
	return a.Id < b.Id
}

// Bearings starts a new (or resumed) feature session and rearms the per-feature guard.
func Bearings(envelope *engine.Envelope) string {
	engine.SetState(featureStepsKey, "1")
	return SmokePrompt()
}

// Smoke checks the per-feature budget after the smoke test.
func Smoke(envelope *engine.Envelope) string {
	if overFeatureBudget() {
		return stopFlow("guarda por feature")
	}
	return PickPrompt()
}

// Pick deterministically selects the next ready feature.
func Pick(envelope *engine.Envelope) string {
	if overFeatureBudget() {
		return stopFlow("guarda por feature")
	}

	next := engine.NextPendingFeature()
	if next == nil {
		// PendingFeatureCount() == 0 is the normal case (handoff would already have
		// closed things out). Pending > 0 is only reachable via a hand-edited
		// feature_list.json outside the graph validated in plan (Write/MarkPassed don't
		// revalidate) — doesn't fake success in that case.
		if engine.PendingFeatureCount() == 0 {
			return done()
		}
		return stopFlow("dependências bloqueadas — nenhuma feature pendente está pronta")
	}

	engine.SetState(currentFeatureIdKey, strconv.Itoa(next.Id))
	engine.SetState(currentFeatureTitleKey, next.Title)
	// Labels the trace with the current feature (see TraceEntry.Label) — without this,
	// every trace.jsonl line only has the global Step, with no indication of which feature
	// it belongs to.
	engine.SetState(engine.TraceLabelKey, fmt.Sprintf("feature:%d", next.Id))
	return ImplementPrompt(*next)
}

// Implement records the driver's summary and attempts automated verification.
func Implement(envelope *engine.Envelope) string {
	if overFeatureBudget() {
		return stopFlow("guarda por feature")
	}

	summary := strings.TrimSpace(arg(envelope))
	if summary != "" {
		engine.SetState(currentFeatureSummaryKey, summary)
	}

	autoVerify := tryAutomatedVerify()
	if autoVerify.Attempted {
		engine.SetState(currentFeatureVerifyKey, autoVerify.Result)
		if autoVerify.Success {
			return completeVerifiedFeature(autoVerify.Result)
		}
		return FixPrompt(autoVerify.Result)
	}

	return VerifyPrompt()
}

// Verify handles the manual self-verify fallback path.
func Verify(envelope *engine.Envelope) string {
	if overFeatureBudget() {
		return stopFlow("guarda por feature")
	}

	// FAILED → back to implementing the SAME feature (correction loop, bounded by the
	// guard). PASSED → the harness performs the deterministic handoff (progress + git)
	// without spending a model turn; if that fails, falls back to the legacy manual-repair
	// prompt.
	result := strings.TrimSpace(arg(envelope))
	upper := strings.ToUpper(result)
	if strings.HasPrefix(upper, "FAIL") {
		return FixPrompt(result)
	}
	if strings.HasPrefix(upper, "PASS") {
		engine.SetState(currentFeatureVerifyKey, result)
		return completeVerifiedFeature(result)
	}

	return VerifyRetryPrompt()
}

// Handoff records the driver's manual handoff confirmation.
func Handoff(envelope *engine.Envelope) string {
	if strings.TrimSpace(arg(envelope)) == "" {
		return HandoffRetryPrompt()
	}

	if id, err := strconv.Atoi(state(currentFeatureIdKey)); err == nil {
		engine.MarkFeaturePassed(id)
	}

	// Any feature still pending? Yes → next session (bearings). No → done.
	if engine.AllFeaturesPassing() {
		return done()
	}
	return BearingsPrompt()
}

// --- guards and termination -------------------------------------------------

// overFeatureBudget increments the session counter and signals whether the per-feature
// ceiling was exceeded.
func overFeatureBudget() bool {
	steps, _ := strconv.Atoi(state(featureStepsKey))
	steps++
	engine.SetState(featureStepsKey, strconv.Itoa(steps))

	if steps > StepsPerFeature {
		fmt.Fprintf(os.Stderr, "[dev] feature '%s' excedeu %d passos; encerrando.\n", state(currentFeatureTitleKey), StepsPerFeature)
		return true
	}
	return false
}

func stopFlow(reason string) string {
	fmt.Fprintf(os.Stderr, "[dev] encerrado por %s. feature_list em .harness/feature_list.json\n", reason)
	return "stop"
}

func done() string {
	fmt.Fprintf(os.Stderr,
		"[dev] todas as %d features passam; concluído. Estado em .harness/feature_list.json\n", len(engine.LoadFeatures()))
	return "stop"
}

func arg(envelope *engine.Envelope) string {
	if envelope != nil && len(envelope.Args) > 0 {
		return envelope.Args[0]
	}
	return ""
}

func argAt(envelope *engine.Envelope, index int, fallback string) string {
	if envelope != nil && len(envelope.Args) > index && strings.TrimSpace(envelope.Args[index]) != "" {
		return envelope.Args[index]
	}
	return fallback
}
