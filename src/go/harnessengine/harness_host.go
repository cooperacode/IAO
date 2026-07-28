package harnessengine

import "fmt"

// RunOptions configures a HarnessHost.Run call. Validators, MaxSteps, and
// ShouldResetOnStart are optional (nil-able) overrides.
type RunOptions struct {
	TraceSnapshotPath  string
	StateSnapshotPath  string
	Validators         map[string]Validator
	MaxSteps           *int
	ShouldResetOnStart func() bool
}

// Run is a flow's reusable entry point. A new domain only needs to define its tasks and
// call Run — all the orchestration (dispatch, guards, transport) lives here.
func Run(args []string, tasks map[string]Action, opts RunOptions) int {
	traceSnapshotPath := opts.TraceSnapshotPath
	if traceSnapshotPath == "" {
		traceSnapshotPath = LastRunTracePath
	}
	stateSnapshotPath := opts.StateSnapshotPath
	if stateSnapshotPath == "" {
		stateSnapshotPath = LastRunStatePath
	}

	result := Dispatch(args, tasks, opts.Validators, opts.MaxSteps, opts.ShouldResetOnStart)

	// Run completed: freezes trajectory AND final state as evidence for later evaluation,
	// before a subsequent flow resets the live trace/state. Each flow publishes to ITS OWN
	// path (refinement to last-run.*, evaluation to last-evaluation.*), so the evaluation
	// never overwrites what it itself consumes.
	if result == "stop" {
		SnapshotTrace(traceSnapshotPath)
		SnapshotState(stateSnapshotPath)
	}

	// The only place that writes to stdout — the harness's transport channel.
	fmt.Println(result)
	return 0
}
