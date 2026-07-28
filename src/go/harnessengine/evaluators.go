package harnessengine

import (
	"fmt"
	"regexp"
	"strings"
)

// Deterministic evaluators (Exact Match, Regex, Trajectory) — the diagram's Evaluator that
// does NOT need an LLM. They run in-process over Trace and HarnessState, cost zero tokens,
// and serve as a gate: only when they pass is it worth escalating to the LLM judge (a
// saving under the token budget).

// Score is the grade for a metric in [0,1]. Passed requires a full match.
type Score struct {
	Metric string
	Value  float64
	Detail string
}

// Passed reports whether the score reached the maximum (1.0).
func (s Score) Passed() bool {
	return s.Value >= 1.0
}

// ExactMatch compares expected and actual, trimmed.
func ExactMatch(expected, actual string) Score {
	value := 0.0
	if strings.TrimSpace(expected) == strings.TrimSpace(actual) {
		value = 1.0
	}
	return Score{Metric: "exact_match", Value: value, Detail: fmt.Sprintf(`esperado="%s" obtido="%s"`, expected, actual)}
}

// MatchesRegex checks whether actual matches pattern.
func MatchesRegex(pattern, actual string) Score {
	value := 0.0
	if matched, err := regexp.MatchString(pattern, actual); err == nil && matched {
		value = 1.0
	}
	return Score{Metric: "regex", Value: value, Detail: pattern}
}

// Trajectory scores the fraction of the expected prefix that matched, in order. A
// step out of sequence stops the count there — trajectory is about the path, not the set.
func Trajectory(expected, actual []string) Score {
	matched := 0
	for i := 0; i < len(expected) && i < len(actual); i++ {
		if expected[i] != actual[i] {
			break
		}
		matched++
	}

	value := 1.0
	if len(expected) > 0 {
		value = float64(matched) / float64(len(expected))
	}
	return Score{Metric: "trajectory", Value: value, Detail: fmt.Sprintf("%d/%d passos na ordem esperada", matched, len(expected))}
}

// Completeness scores the fraction of required domain keys that were filled in the final state.
func Completeness(state HarnessState, requiredKeys []string) Score {
	filled := 0
	for _, k := range requiredKeys {
		if strings.TrimSpace(state.Data[k]) != "" {
			filled++
		}
	}
	value := 1.0
	if len(requiredKeys) > 0 {
		value = float64(filled) / float64(len(requiredKeys))
	}
	return Score{Metric: "completeness", Value: value, Detail: fmt.Sprintf("%d/%d chaves preenchidas", filled, len(requiredKeys))}
}

// StepBudget reports whether the run ended in TraceOutcome.Stop without hitting the step
// ceiling nor the time ceiling (TraceOutcome.Timeout) — both would be indistinguishable
// from a simply-incomplete trajectory if not checked separately.
func StepBudget(trace []TraceEntry) Score {
	hitBudget, hitTimeout, terminated := false, false, false
	for _, e := range trace {
		switch e.Outcome {
		case TraceOutcome.Budget:
			hitBudget = true
		case TraceOutcome.Timeout:
			hitTimeout = true
		case TraceOutcome.Stop:
			terminated = true
		}
	}

	value := 0.0
	detail := "não terminou"
	switch {
	case hitBudget:
		detail = "cortado pelo teto de passos"
	case hitTimeout:
		detail = "cortado pelo teto de tempo (timeout)"
	case terminated:
		detail = "concluído dentro do teto"
	}
	if !hitBudget && !hitTimeout && terminated {
		value = 1.0
	}

	return Score{Metric: "budget", Value: value, Detail: detail}
}

// CommandsOf returns the trace's commands in order, by default skipping corrective-error turns.
func CommandsOf(trace []TraceEntry, includeErrors bool) []string {
	commands := make([]string, 0, len(trace))
	for _, e := range trace {
		if includeErrors || e.Outcome != TraceOutcome.Error {
			commands = append(commands, e.Command)
		}
	}
	return commands
}
