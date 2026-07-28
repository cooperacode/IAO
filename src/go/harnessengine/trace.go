package harnessengine

import (
	"bufio"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"time"
)

// Writes one line per loop turn to .harness/trace.jsonl. It is the basis of both telemetry
// and the trajectory evaluator: StateStore only keeps the final state — it overwrites Data
// on every step —, so without this recorded sequence there is no way to evaluate the path
// the agent took.
//
// Cost: zero tokens and one append write per invocation.
const (
	traceDir      = ".harness"
	traceFilePath = ".harness/trace.jsonl"

	// LastRunTracePath is the frozen trajectory of the last run that ended in `stop`.
	// HarnessHost writes here when the producer flow completes, so another flow (the
	// evaluation) can read the evidence even after resetting the live trace.jsonl on its
	// own start.
	LastRunTracePath = ".harness/last-run.trace.jsonl"

	// LastEvaluationTracePath is the frozen trajectory of the last EVALUATION run. Its own
	// path so a re-evaluation doesn't overwrite the refinement's evidence in
	// LastRunTracePath.
	LastEvaluationTracePath = ".harness/last-evaluation.trace.jsonl"
)

// TraceOutcome enumerates the possible outcomes of a step, recorded in TraceEntry.Outcome.
var TraceOutcome = struct {
	Instruction string // advanced to the next step
	Stop        string // normal flow termination
	Error       string // typed error returned to the driver
	Budget      string // cut by the step ceiling
	Timeout     string // cut by the per-step time ceiling
}{
	Instruction: "instruction",
	Stop:        "stop",
	Error:       "error",
	Budget:      "budget",
	Timeout:     "timeout",
}

// TraceEntry is one loop turn: step, received command, outcome, cost (UTF-8 octets of the
// emitted instruction), recording time, and PrevHash — the lowercase hex SHA-256 of the
// previous trace line, forming the hash chain (RFC §6.13) that makes retroactive
// edit/removal detectable. Label is the optional, domain-agnostic tag (e.g. "feature:3")
// that solves the same pain as StateStore: Step is a counter global to the whole run, it
// doesn't identify WHICH unit of work the step belongs to.
type TraceEntry struct {
	Step             int    `json:"step"`
	Command          string `json:"command"`
	Outcome          string `json:"outcome"`
	InstructionChars int    `json:"instructionChars"`
	Timestamp        string `json:"timestamp"`
	PrevHash         string `json:"prevHash"`
	Label            string `json:"label"`
}

// ResetTrace truncates the trace at the start of a new workflow (paired with ResetState).
func ResetTrace() {
	if !fileExists(traceFilePath) {
		return
	}
	if err := os.Remove(traceFilePath); err != nil {
		fmt.Fprintf(os.Stderr, "[Trace] falha ao limpar: %s\n", err)
	}
}

// AppendTrace appends a labeled trace entry.
func AppendTrace(step int, command, outcome string, instructionChars int, label string) {
	if err := ensureDir(traceDir); err != nil {
		fmt.Fprintf(os.Stderr, "[Trace] falha ao gravar: %s\n", err)
		return
	}

	entry := TraceEntry{
		Step:             step,
		Command:          command,
		Outcome:          outcome,
		InstructionChars: instructionChars,
		Timestamp:        time.Now().UTC().Format("2006-01-02T15:04:05.000000Z"),
		PrevHash:         computePrevHash(),
		Label:            label,
	}

	line, err := json.Marshal(entry)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[Trace] falha ao gravar: %s\n", err)
		return
	}

	f, err := os.OpenFile(traceFilePath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[Trace] falha ao gravar: %s\n", err)
		return
	}
	defer f.Close()

	// Single write for the whole line (JSON + newline already assembled) — the guarantee
	// that the event is atomic at the file level.
	if _, err := f.WriteString(string(line) + "\n"); err != nil {
		fmt.Fprintf(os.Stderr, "[Trace] falha ao gravar: %s\n", err)
	}
}

// computePrevHash implements the hash chain (RFC §6.13): each line references the
// lowercase hex SHA-256 of the previous line (exactly as written, byte for byte), making
// any retroactive edit/removal of the trace detectable — the chain breaks from the altered
// point on. Genesis (the file's first entry, including right after a ResetTrace) uses 64
// zeros.
func computePrevHash() string {
	lastLine := lastNonEmptyLine()
	if lastLine == "" {
		return strings.Repeat("0", 64)
	}
	sum := sha256.Sum256([]byte(lastLine))
	return hex.EncodeToString(sum[:])
}

func lastNonEmptyLine() string {
	if !fileExists(traceFilePath) {
		return ""
	}
	data, err := os.ReadFile(traceFilePath)
	if err != nil {
		return ""
	}
	lines := strings.Split(string(data), "\n")
	for i := len(lines) - 1; i >= 0; i-- {
		if strings.TrimSpace(lines[i]) != "" {
			return lines[i]
		}
	}
	return ""
}

// SnapshotTrace freezes the live trace at destination — the completed run's evidence.
func SnapshotTrace(destination string) {
	if !fileExists(traceFilePath) {
		return
	}
	if err := ensureDir(traceDir); err != nil {
		fmt.Fprintf(os.Stderr, "[Trace] falha ao congelar: %s\n", err)
		return
	}
	if err := copyAtomic(traceFilePath, destination); err != nil {
		fmt.Fprintf(os.Stderr, "[Trace] falha ao congelar: %s\n", err)
	}
}

// LoadTrace re-reads the live trace in write order.
func LoadTrace() []TraceEntry {
	return LoadTraceFrom(traceFilePath)
}

// LoadTraceFrom re-reads a trace from an arbitrary path — input to the evaluators (e.g. the
// snapshot).
func LoadTraceFrom(path string) []TraceEntry {
	if !fileExists(path) {
		return []TraceEntry{}
	}
	f, err := os.Open(path)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[Trace] falha ao carregar: %s\n", err)
		return []TraceEntry{}
	}
	defer f.Close()

	entries := []TraceEntry{}
	scanner := bufio.NewScanner(f)
	scanner.Buffer(make([]byte, 0, 64*1024), 10*1024*1024)
	for scanner.Scan() {
		line := scanner.Text()
		if strings.TrimSpace(line) == "" {
			continue
		}
		var entry TraceEntry
		if err := json.Unmarshal([]byte(line), &entry); err == nil {
			entries = append(entries, entry)
		}
	}
	return entries
}
