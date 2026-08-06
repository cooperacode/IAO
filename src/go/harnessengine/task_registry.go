package harnessengine

import (
	"fmt"
	"sort"
	"strings"
	"time"
)

// Action is a task: given the parsed envelope (nil if parsing failed), returns the next
// instruction (or "stop").
type Action func(*Envelope) string

// DefaultMaxSteps is the step ceiling: prevents an infinite loop that would burn tokens
// indefinitely. Comes from harness.json (or its default) — see HarnessConfig.
func DefaultMaxSteps() int {
	return CurrentConfig().MaxSteps
}

// Dispatch is the domain-agnostic dispatcher: envelope parsing, iteration guard, and typed
// error handling.
func Dispatch(
	args []string,
	actions map[string]Action,
	validators map[string]Validator,
	maxSteps *int,
	shouldResetOnStart func() bool,
) string {
	// Argv present → classic transport (backward compatible). Empty argv → reads the
	// envelope from the file-based inbox, the transport that eliminates the shell-quoting
	// hang (see Inbox).
	fromInbox := len(args) == 0
	arg0 := ""
	if len(args) > 0 {
		arg0 = args[0]
	} else {
		arg0 = ReadInbox()
	}

	var envelope *Envelope
	if strings.TrimSpace(arg0) != "" {
		envelope = ParseEnvelope(arg0)
	}

	// Only consumes the inbox when parsing succeeded — a broken JSON must produce the
	// corrective ERROR and remain available for inspection, not silently disappear.
	if fromInbox && envelope != nil {
		ConsumeInbox()
	}

	// Budget stops remain terminal. A timeout or fault is recoverable only through an
	// explicit `start`: the abandoned worker (timed out, or crashed on a harness bug)
	// belonged to the previous process, and the driver is deliberately asking the flow to
	// resume or restart — never by silently resending the same command.
	if terminal := TerminalReason(); terminal != "" {
		if (terminal == "timeout" || terminal == "fault") && envelope != nil && envelope.Value == "start" {
			ClearTerminal()
		} else {
			LogError(fmt.Sprintf("[harness] run already stopped (%s); refusing another turn.", terminal))
			return "stop"
		}
	}

	if envelope != nil && envelope.Value == "start" {
		// A new workflow starts from scratch — state and trace are truncated together. But
		// a "start" also arrives when a fresh session (e.g. a Development per-feature hard
		// reset) reopens a run in progress — in that case it's a RESUME, not a start, and
		// truncating here would throw away the trace/step accumulated by previous
		// features. The flow decides via shouldResetOnStart (it knows whether there's
		// pending work); with no predicate, the default is to always reset (backward
		// compatible with single-shot flows).
		shouldReset := true
		if shouldResetOnStart != nil {
			shouldReset = shouldResetOnStart()
		}
		if shouldReset {
			ResetState()
			ResetTrace()
			ResetHarnessLog()
		}

		// The driver context (e.g. {"driver":"claude code"}) is born here and survives in
		// StateStore — PromptFormatter reinjects it into every output until the next
		// "start". Independent of the reset above: even on a resume, the current driver
		// must prevail.
		if len(envelope.Context) > 0 {
			SetContext(envelope.Context)
		}
	}
	var observedContextUsage *ContextUsage
	if envelope != nil {
		usage := envelope.ContextUsage
		if usage == nil {
			usage = ContextUsageFromEnvironment()
		}
		observedContextUsage = usage
	} else {
		observedContextUsage = ContextUsageFromEnvironment()
	}
	ObserveContextUsage(observedContextUsage)

	// Iteration guard — hard stop under the team's token budget.
	step := IncrementStep()

	costChars := LoadState().CostChars
	command := "(unparsed)"
	if envelope != nil && envelope.Value != "" {
		command = envelope.Value
	}

	// Logged BEFORE the action runs: trace.jsonl only gets a line once the step completes,
	// so a slow or hung step (or one that faults below) would otherwise leave zero evidence
	// the harness ever picked it up — the "feels idle" gap.
	LogInfo(fmt.Sprintf("[step %d] enter '%s'", step, command))

	result, outcome := resolve(envelope, step, costChars, actions, validators, maxSteps)

	// UTF-8 octets, not runes (RFC Appendix B item 1): measures what actually crosses the
	// transport, with the same meaning as .NET (UTF-8 byte count) and Rust (String::len()).
	resultBytes := len([]byte(result))

	LogInfo(fmt.Sprintf("[step %d] exit outcome=%s bytes=%d", step, outcome, resultBytes))

	// One line per loop turn: feeds telemetry and the trajectory evaluator. Label is
	// re-read (not from the Load() snapshot above) because the action itself may have just
	// set it (e.g. Pick choosing this step's feature).
	label := ""
	if v := GetState(TraceLabelKey); v != nil {
		label = *v
	}
	AppendTrace(step, command, outcome, resultBytes, label, observedContextUsage)

	// The instruction's cost is only known here now — it feeds the accumulator the next
	// turn's guard will check.
	AddCost(resultBytes)

	return result
}

func resolve(
	envelope *Envelope,
	step, costChars int,
	actions map[string]Action,
	validators map[string]Validator,
	maxSteps *int,
) (string, string) {
	// Effective step ceiling: a per-call override (e.g. a long-running flow like
	// Development, which needs more slack) takes precedence over harness.json's global
	// one. With no override, the config's value applies.
	effectiveMaxSteps := DefaultMaxSteps()
	if maxSteps != nil {
		effectiveMaxSteps = *maxSteps
	}
	if step > effectiveMaxSteps {
		LogError(fmt.Sprintf("[harness] step limit of %d reached; stopping.", effectiveMaxSteps))
		MarkTerminal("budget")
		return "stop", TraceOutcome.Budget
	}

	// Cost ceiling, a second guard beyond the step one. Emitted-instruction chars are the
	// only measure: it's what the engine can attest on its own. Real tokens live in the
	// caller's billing metadata — an LLM driver has no way to honestly report them.
	config := CurrentConfig()
	if config.MaxInstructionChars > 0 && costChars > config.MaxInstructionChars {
		LogError(fmt.Sprintf("[harness] instruction char limit of %d reached (%d); stopping.",
			config.MaxInstructionChars, costChars))
		MarkTerminal("budget")
		return "stop", TraceOutcome.Budget
	}

	// Typed error instead of silent "stop": the model receives the cause and can resend
	// the right command (corrective loop, not silent termination).
	if envelope == nil {
		return errorInstruction("Could not parse the received JSON.", actions), TraceOutcome.Error
	}

	action, ok := actions[envelope.Value]
	if !ok {
		return errorInstruction(fmt.Sprintf("The command '%s' does not exist.", envelope.Value), actions), TraceOutcome.Error
	}

	// Contextual validation: the command exists, but does the VALUE meet the task's
	// expectation? Failed → same corrective-error path as above; the driver fixes and resends.
	if validators != nil {
		if validator, ok := validators[envelope.Value]; ok {
			if rejected := validator(*envelope); !rejected.Ok {
				return errorInstruction(fmt.Sprintf(
					"The command '%s' was rejected: %s Fix the 'args' content and resend the same command.",
					envelope.Value, rejected.Reason), actions), TraceOutcome.Error
			}
		}
	}

	// Time guard: a stuck task (infinite loop in domain logic) would hang the process
	// indefinitely. runWithTimeout enforces the per-step ceiling; a timeout becomes a typed
	// error, caught here, following the same graceful path as the budget cut: stderr
	// diagnostic + "stop" on stdout (the channel the IDE client reads). A panic inside the
	// action itself (a real bug, not a driver protocol error) is recovered the same way —
	// see runProtected — and reported as a distinct "fault" outcome instead of crashing the
	// process or being silently mislabeled as a timeout.
	result, err := runWithTimeout(action, envelope, config.TimeoutMs)
	if err != nil {
		LogError(fmt.Sprintf("[harness] %s", err))
		if _, ok := err.(*HarnessFaultError); ok {
			MarkTerminal("fault")
			return "stop", TraceOutcome.Fault
		}
		MarkTerminal("timeout")
		return "stop", TraceOutcome.Timeout
	}

	if result == "stop" {
		return result, TraceOutcome.Stop
	}
	return result, TraceOutcome.Instruction
}

// runWithTimeout: the task is a synchronous, OPAQUE closure — it does not cooperate with
// cancellation. Go cannot safely abort stuck synchronous code, so the only real preemptive
// timeout is to run it on a goroutine and ABANDON whatever hangs. The goroutine leaks if it
// never finishes, but it dies with the process once main returns "stop" — the same model as
// .NET's Task.Run (threadpool) and Rust's spawned thread.
func runWithTimeout(action Action, envelope *Envelope, timeoutMs int) (string, error) {
	if timeoutMs <= 0 {
		return runProtected(action, envelope) // guard disabled — no goroutine overhead
	}

	type outcome struct {
		result string
		err    error
	}
	done := make(chan outcome, 1)
	go func() {
		result, err := runProtected(action, envelope)
		done <- outcome{result, err}
	}()

	select {
	case o := <-done:
		return o.result, o.err
	case <-time.After(time.Duration(timeoutMs) * time.Millisecond):
		return "", &HarnessTimeoutError{TimeoutMs: timeoutMs}
	}
}

// runProtected recovers a panic raised inside the action — a bug in task logic, not a driver
// protocol error — converting it into a typed HarnessFaultError instead of letting it crash
// the whole process (direct path) or silently kill the goroutine before it can report back
// (timeout path, where an unrecovered panic would otherwise terminate the process outright).
func runProtected(action Action, envelope *Envelope) (result string, err error) {
	defer func() {
		if r := recover(); r != nil {
			err = &HarnessFaultError{Reason: fmt.Sprintf("%v", r)}
		}
	}()
	return action(envelope), nil
}

func errorInstruction(reason string, actions map[string]Action) string {
	// Sorted for determinism: Go map iteration order is not stable across runs, and the
	// message is more useful already sorted.
	keys := make([]string, 0, len(actions))
	for k := range actions {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	valid := strings.Join(keys, ", ")

	return fmt.Sprintf(
		"HARNESS PROTOCOL ERROR: %s Valid commands: %s. "+
			"Review the 'value' field in your JSON response (reply with the JSON only, "+
			"no code fences or commentary) and resend the command.", reason, valid)
}
