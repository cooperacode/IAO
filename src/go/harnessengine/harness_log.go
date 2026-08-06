package harnessengine

import (
	"fmt"
	"os"
	"time"
)

// Append-only, human-readable engine log at .harness/harness.log — persisted counterpart to
// what today only reaches ephemeral stderr (LogError), plus the step entry/exit markers
// (LogInfo, written by Dispatch) that make an in-flight step observable before it completes.
// Trace only records a COMPLETED turn — during a slow step, or one that crashes mid-flight,
// trace.jsonl alone gives no evidence the harness ever picked up the work. This file is that
// evidence.
//
// Deliberately separate from trace.jsonl: the trace is a hash-chained, one-line-per-turn
// audit artifact consumed by evaluators and cost-correlation tooling — doubling it with
// entry/exit lines would break that "one line = one turn" contract for every consumer.
// harness.log carries no such contract; it's free-form and append-only.
const (
	harnessLogDir      = ".harness"
	harnessLogFilePath = ".harness/harness.log"
)

// ResetHarnessLog truncates the log at the start of a new workflow (alongside ResetTrace).
func ResetHarnessLog() {
	if !fileExists(harnessLogFilePath) {
		return
	}
	if err := os.Remove(harnessLogFilePath); err != nil {
		fmt.Fprintf(os.Stderr, "[HarnessLog] failed to clear: %s\n", err)
	}
}

// LogInfo records a liveness/diagnostic event (step entry/exit) — file only, no stderr echo
// per turn.
func LogInfo(message string) {
	writeHarnessLog("INFO", message)
}

// LogError records a harness-level failure — protocol errors, guard cutoffs, store I/O
// failures, unhandled faults. Writes to stderr too (existing visible behavior every call
// site already relied on) so this is a drop-in replacement for the raw fmt.Fprintf(os.Stderr,
// ...) calls scattered across the engine.
func LogError(message string) {
	fmt.Fprintln(os.Stderr, message)
	writeHarnessLog("ERROR", message)
}

func writeHarnessLog(level, message string) {
	if err := ensureDir(harnessLogDir); err != nil {
		fmt.Fprintf(os.Stderr, "[HarnessLog] failed to write: %s\n", err)
		return
	}

	f, err := os.OpenFile(harnessLogFilePath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[HarnessLog] failed to write: %s\n", err)
		return
	}
	defer f.Close()

	line := fmt.Sprintf("[%s] [%s] %s\n", time.Now().UTC().Format("2006-01-02T15:04:05.000000Z"), level, message)
	if _, err := f.WriteString(line); err != nil {
		fmt.Fprintf(os.Stderr, "[HarnessLog] failed to write: %s\n", err)
	}
}
