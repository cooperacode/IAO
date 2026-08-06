package main

import (
	"os"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

// "long-running agent" pattern: initializer + loop of fresh sessions, one feature at a
// time. No orchestration here — dispatch, guards, and transport live in harnessengine.
// start → plan → [implement → verify(auto-handoff)]*
func main() {
	args := os.Args[1:]

	tasks := map[string]engine.Action{
		"start":     func(*engine.Envelope) string { return Start() },
		"plan":      Plan,
		"bearings":  Bearings,
		"smoke":     Smoke,
		"pick":      Pick,
		"implement": Implement,
		"verify":    Verify,
		"handoff":   Handoff,
	}

	// Contextual expectation per command; a rejection becomes a corrective error (the
	// driver fixes and resends). `pick` has no validator — it doesn't carry a driver
	// artifact (the selection is the harness's). `plan` no longer carries the feature
	// array in its args (see planFilePath in tasks.go) — Plan() itself validates the
	// file's content and re-requests it via PlanRetryPrompt, so no envelope-arg
	// validator applies here either.
	validators := map[string]engine.Validator{}

	// Own snapshots: if this flow shares .harness/ with other flows (same workspace), it
	// must NOT overwrite the last-run.* another flow consumes. Freezes at its own path.
	// MaxSteps: override of the global ceiling (12) — this flow is long-running and needs
	// slack for the loop.
	// ShouldResetOnStart: a "start" also arrives on the per-feature hard reset (a fresh
	// session reopening a run in progress) — it's only a genuinely new run when there's no
	// pending feature.
	maxSteps := StepBudget
	shouldResetOnStart := func() bool { return engine.PendingFeatureCount() == 0 }

	code := engine.Run(args, tasks, engine.RunOptions{
		TraceSnapshotPath:  ".harness/last-development.trace.jsonl",
		StateSnapshotPath:  ".harness/last-development.state.json",
		Validators:         validators,
		MaxSteps:           &maxSteps,
		ShouldResetOnStart: shouldResetOnStart,
	})

	os.Exit(code)
}
