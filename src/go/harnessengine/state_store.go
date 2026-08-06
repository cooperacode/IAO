package harnessengine

import (
	"encoding/json"
	"fmt"
	"os"
)

// Each harness invocation is a fresh, memoryless process. This store persists the
// accumulated state (step counter + domain data) to a file, so the envelope carried by the
// model stays minimal — a token saving: the model passes a key, not the whole state, each
// loop turn.
const (
	stateDir      = ".harness"
	stateFilePath = ".harness/state.json"

	// LastRunStatePath is the frozen final state of the last completed run. Exists for the
	// same reason as LastRunTracePath: any flow's `start` resets the live state.json, so
	// the evaluation (which checks completeness) needs to read domain keys from a stable
	// snapshot, not the file its own start just zeroed.
	LastRunStatePath = ".harness/last-run.state.json"

	// LastEvaluationStatePath is the frozen final state of the last evaluation run — its
	// own path, so it never overwrites the refinement's.
	LastEvaluationStatePath = ".harness/last-evaluation.state.json"

	// TraceLabelKey is the conventional key in HarnessState.Data for the label TaskRegistry
	// propagates to Trace on each step (see TraceEntry.Label). Deliberately generic: the
	// engine doesn't know what a "feature" is — it only re-reads this key if the flow has
	// set it (e.g. DevelopmentTasks.Pick).
	TraceLabelKey = "trace_label"
)

// LoadState loads the live state.
func LoadState() HarnessState {
	return LoadStateFrom(stateFilePath)
}

// LoadStateFrom loads a state from an arbitrary path (e.g. a golden-set case's evidence).
func LoadStateFrom(path string) HarnessState {
	if fileExists(path) {
		data, err := os.ReadFile(path)
		if err == nil {
			var state HarnessState
			if err := json.Unmarshal(data, &state); err == nil {
				if state.Data == nil {
					state.Data = map[string]string{}
				}
				return state
			}
			LogError(fmt.Sprintf("[StateStore] failed to load: %s", err))
		} else {
			LogError(fmt.Sprintf("[StateStore] failed to load: %s", err))
		}
	}

	return NewHarnessState(0, nil)
}

// SaveState persists state to the live state.json.
func SaveState(state HarnessState) {
	if err := ensureDir(stateDir); err != nil {
		LogError(fmt.Sprintf("[StateStore] failed to save: %s", err))
		return
	}
	data, err := json.Marshal(state)
	if err != nil {
		LogError(fmt.Sprintf("[StateStore] failed to save: %s", err))
		return
	}
	if err := writeAtomic(stateFilePath, string(data)); err != nil {
		LogError(fmt.Sprintf("[StateStore] failed to save: %s", err))
	}
}

// ResetState truncates the live state back to step 0 with no data.
func ResetState() {
	SaveState(NewHarnessState(0, nil))
}

// SnapshotState freezes the live state.json at destination — the evidence of the completed
// run.
func SnapshotState(destination string) {
	if !fileExists(stateFilePath) {
		return
	}
	if err := ensureDir(stateDir); err != nil {
		LogError(fmt.Sprintf("[StateStore] failed to freeze: %s", err))
		return
	}
	if err := copyAtomic(stateFilePath, destination); err != nil {
		LogError(fmt.Sprintf("[StateStore] failed to freeze: %s", err))
	}
}

// IncrementStep bumps the step counter and returns the new value.
func IncrementStep() int {
	state := LoadState()
	next := state.Step + 1
	state.Step = next
	SaveState(state)
	return next
}

// AddCost adds chars to the run's accumulated cost and returns the new total — input to
// the cost ceiling in TaskRegistry. UTF-8 octets of the emitted instruction are the only
// measure (RFC Appendix B item 1): it's what the engine can attest to on its own, without
// relying on driver self-reporting, with the same meaning across engines.
func AddCost(chars int) int {
	state := LoadState()
	state.CostChars += chars
	SaveState(state)
	return state.CostChars
}

// SetState sets a domain data key.
func SetState(key, value string) {
	state := LoadState()
	state.Data[key] = value
	SaveState(state)
}

// GetState reads a domain data key, or nil if absent.
func GetState(key string) *string {
	state := LoadState()
	if value, ok := state.Data[key]; ok {
		return &value
	}
	return nil
}

// SetContext persists the driver context captured on `start` (see TaskRegistry).
func SetContext(context map[string]string) {
	state := LoadState()
	state.Context = context
	SaveState(state)
}

// GetContext returns the persisted driver context, for PromptFormatter to reinject into
// every output.
func GetContext() map[string]string {
	return LoadState().Context
}

// MarkTerminal latches a hard-stop reason across process boundaries.
func MarkTerminal(reason string) {
	state := LoadState()
	state.TerminalReason = reason
	SaveState(state)
}

// ClearTerminal clears a recoverable timeout latch after an explicit start.
func ClearTerminal() {
	state := LoadState()
	if state.TerminalReason != "" {
		state.TerminalReason = ""
		SaveState(state)
	}
}

// TerminalReason returns the latched hard-stop reason, if any.
func TerminalReason() string {
	return LoadState().TerminalReason
}
