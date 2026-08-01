package harnessengine

import (
	"fmt"
	"os"
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

	// Budget stops remain terminal. A timeout is recoverable only through an explicit
	// `start`: the timed-out worker was abandoned with the previous process, and the
	// driver is deliberately asking the flow to resume or restart.
	if terminal := TerminalReason(); terminal != "" {
		if terminal == "timeout" && envelope != nil && envelope.Value == "start" {
			ClearTerminal()
		} else {
			fmt.Fprintf(os.Stderr, "[harness] run already stopped (%s); refusing another turn.\n", terminal)
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

	result, outcome := resolve(envelope, step, costChars, actions, validators, maxSteps)

	// UTF-8 octets, not runes (RFC Appendix B item 1): measures what actually crosses the
	// transport, with the same meaning as .NET (UTF-8 byte count) and Rust (String::len()).
	resultBytes := len([]byte(result))

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
		fmt.Fprintf(os.Stderr, "[harness] step limit of %d reached; stopping.\n", effectiveMaxSteps)
		MarkTerminal("budget")
		return "stop", TraceOutcome.Budget
	}

	// Cost ceiling, a second guard beyond the step one. Emitted-instruction chars are the
	// only measure: it's what the engine can attest on its own. Real tokens live in the
	// caller's billing metadata — an LLM driver has no way to honestly report them.
	config := CurrentConfig()
	if config.MaxInstructionChars > 0 && costChars > config.MaxInstructionChars {
		fmt.Fprintf(os.Stderr, "[harness] instruction char limit of %d reached (%d); stopping.\n",
			config.MaxInstructionChars, costChars)
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
	// diagnostic + "stop" on stdout (the channel the IDE client reads).
	result, err := runWithTimeout(action, envelope, config.TimeoutMs)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[harness] %s\n", err)
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
		return action(envelope), nil // guard disabled — no goroutine overhead
	}

	done := make(chan string, 1)
	go func() {
		done <- action(envelope)
	}()

	select {
	case result := <-done:
		return result, nil
	case <-time.After(time.Duration(timeoutMs) * time.Millisecond):
		return "", &HarnessTimeoutError{TimeoutMs: timeoutMs}
	}
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
