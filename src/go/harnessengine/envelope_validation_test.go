package harnessengine

import (
	"strings"
	"testing"
)

func TestMinLines_CountsLiteralAndRealBreaks(t *testing.T) {
	validator := MinLines(2, "lista de histórias")

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
	validator := ContainsNumber("estimativas")

	if !validator(NewEnvelope("tool", "risks", []string{"5 pontos"})).Ok {
		t.Error("expected pass with digit")
	}
	if validator(NewEnvelope("tool", "risks", []string{"sem pontos"})).Ok {
		t.Error("expected fail without digit")
	}
}

func TestMatches_CaseInsensitive(t *testing.T) {
	validator := Matches("READY|NOT READY", "veredito do DoR")

	if !validator(NewEnvelope("tool", "finalize", []string{"Veredito: ready com ressalva"})).Ok {
		t.Error("expected case-insensitive match")
	}
	if validator(NewEnvelope("tool", "finalize", []string{"aprovado"})).Ok {
		t.Error("expected no match")
	}
}

func TestMatches_AnchoredPattern_RejectsPrefixOnlyContent(t *testing.T) {
	validator := Matches(`^(PASS\b|FAIL\b)`, "veredito")

	if !validator(NewEnvelope("command", "verify", []string{"PASS: testes verdes"})).Ok {
		t.Error("expected PASS to match")
	}
	if !validator(NewEnvelope("command", "verify", []string{"FAIL: testes vermelhos"})).Ok {
		t.Error("expected FAIL to match")
	}
	if validator(NewEnvelope("command", "verify", []string{"rodei os testes e deu PASS"})).Ok {
		t.Error("expected non-anchored content to fail")
	}
}

func TestAllOf_FailsOnFirstReason(t *testing.T) {
	validator := AllOf(NotEmpty("estimativas"), ContainsNumber("estimativas com pontos"))

	result := validator(NewEnvelope("tool", "risks", []string{"sem numeros"}))
	if result.Ok {
		t.Fatal("expected failure")
	}
	if !strings.Contains(result.Reason, "número") {
		t.Errorf("unexpected reason: %s", result.Reason)
	}
}
