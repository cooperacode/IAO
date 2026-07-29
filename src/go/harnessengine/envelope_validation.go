package harnessengine

import (
	"fmt"
	"regexp"
	"strings"
)

// ValidationResult is the result of a contextual validation: ok, or the rejection reason
// (for the corrective error).
type ValidationResult struct {
	Ok     bool
	Reason string
}

// ValidationPass is the successful ValidationResult.
var ValidationPass = ValidationResult{Ok: true}

// ValidationFail builds a failing ValidationResult with the given reason.
func ValidationFail(reason string) ValidationResult {
	return ValidationResult{Ok: false, Reason: reason}
}

// Validator is a deterministic, cheap predicate over an Envelope's returned value —
// checked BEFORE persisting it and advancing the flow. Failed → TaskRegistry returns a
// typed corrective error and the driver resends (corrective loop, not silent termination).
//
// Deep semantic validation is still the LLM judge's job during evaluation; only what is
// checkable in code, at zero token cost, lives here.
type Validator func(Envelope) ValidationResult

// NotEmpty passes when the first arg exists and is not empty/whitespace.
func NotEmpty(expectation string) Validator {
	return func(e Envelope) ValidationResult {
		if firstArg(e) != "" {
			return ValidationPass
		}
		return ValidationFail(fmt.Sprintf("The expected argument came back empty. Expected: %s.", expectation))
	}
}

// MinLines passes when the first arg has at least count non-empty lines (counting literal \n too).
func MinLines(count int, expectation string) Validator {
	return func(e Envelope) ValidationResult {
		lines := countLines(firstArg(e))
		if lines >= count {
			return ValidationPass
		}
		return ValidationFail(fmt.Sprintf(
			"The argument has %d non-empty line(s), but the task expects at least %d. Expected: %s.", lines, count, expectation))
	}
}

var digitRegexp = regexp.MustCompile(`\d`)

// ContainsNumber passes when the first arg contains at least one digit.
func ContainsNumber(expectation string) Validator {
	return func(e Envelope) ValidationResult {
		if digitRegexp.MatchString(firstArg(e)) {
			return ValidationPass
		}
		return ValidationFail(fmt.Sprintf("The argument does not contain any number. Expected: %s.", expectation))
	}
}

// Matches passes when the first arg matches pattern (case-insensitive).
func Matches(pattern, expectation string) Validator {
	re := regexp.MustCompile("(?i)" + pattern)
	return func(e Envelope) ValidationResult {
		if re.MatchString(firstArg(e)) {
			return ValidationPass
		}
		return ValidationFail(fmt.Sprintf("The argument does not match the expected format. Expected: %s.", expectation))
	}
}

// AllOf composes validators: all must pass; the first failure supplies the reason.
func AllOf(validators ...Validator) Validator {
	return func(e Envelope) ValidationResult {
		for _, v := range validators {
			if result := v(e); !result.Ok {
				return result
			}
		}
		return ValidationPass
	}
}

func firstArg(e Envelope) string {
	if len(e.Args) == 0 {
		return ""
	}
	return strings.TrimSpace(e.Args[0])
}

// countLines counts non-empty lines, splitting on both literal "\n" markers and real
// newlines — artifacts travel as a single-line JSON string with literal \n (see the
// "Compact" warning in the flows).
func countLines(value string) int {
	normalized := strings.ReplaceAll(value, "\\n", "\n")
	count := 0
	for _, line := range strings.Split(normalized, "\n") {
		if strings.TrimSpace(line) != "" {
			count++
		}
	}
	return count
}
