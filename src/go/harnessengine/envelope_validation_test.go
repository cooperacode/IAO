package harnessengine

import (
	"strings"
	"testing"
)

func TestMinLines_CountsLiteralAndRealBreaks(t *testing.T) {
	validator := MinLines(2, "list of stories")

	escaped := NewEnvelope("tool", "acceptance", []string{`1. a\n2. b`})
	real := NewEnvelope("tool", "acceptance", []string{"1. a\n2. b"})
	single := NewEnvelope("tool", "acceptance", []string{"1. a"})

	if !validator(escaped).Ok {
		t.Error("expected escaped newlines to count")
	}
	if !validator(real).Ok {
		t.Error("expected real newlines to count")
	}
	if validator(single).Ok {
		t.Error("expected single line to fail")
	}
}

func TestContainsNumber_RequiresAtLeastOneDigit(t *testing.T) {
	validator := ContainsNumber("estimates")

	if !validator(NewEnvelope("tool", "risks", []string{"5 points"})).Ok {
		t.Error("expected pass with digit")
	}
	if validator(NewEnvelope("tool", "risks", []string{"no points"})).Ok {
		t.Error("expected fail without digit")
	}
}

func TestMatches_CaseInsensitive(t *testing.T) {
	validator := Matches("READY|NOT READY", "DoR verdict")

	if !validator(NewEnvelope("tool", "finalize", []string{"Verdict: ready with reservation"})).Ok {
		t.Error("expected case-insensitive match")
	}
	if validator(NewEnvelope("tool", "finalize", []string{"approved"})).Ok {
		t.Error("expected no match")
	}
}

func TestMatches_AnchoredPattern_RejectsPrefixOnlyContent(t *testing.T) {
	validator := Matches(`^(PASS\b|FAIL\b)`, "verdict")

	if !validator(NewEnvelope("command", "verify", []string{"PASS: green tests"})).Ok {
		t.Error("expected PASS to match")
	}
	if !validator(NewEnvelope("command", "verify", []string{"FAIL: red tests"})).Ok {
		t.Error("expected FAIL to match")
	}
	if validator(NewEnvelope("command", "verify", []string{"I ran the tests and got PASS"})).Ok {
		t.Error("expected non-anchored content to fail")
	}
}

func TestAllOf_FailsOnFirstReason(t *testing.T) {
	validator := AllOf(NotEmpty("estimates"), ContainsNumber("estimates with points"))

	result := validator(NewEnvelope("tool", "risks", []string{"no numbers"}))
	if result.Ok {
		t.Fatal("expected failure")
	}
	if !strings.Contains(result.Reason, "number") {
		t.Errorf("unexpected reason: %s", result.Reason)
	}
}
