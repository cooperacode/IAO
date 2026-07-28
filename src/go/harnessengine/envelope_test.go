package harnessengine

import (
	"strings"
	"testing"
)

func TestParseEnvelope_ValidJSON_FillsFields(t *testing.T) {
	e := ParseEnvelope(`{"type":"tool","value":"classify","args":["Login"]}`)
	if e == nil {
		t.Fatal("expected non-nil envelope")
	}
	if e.Type != "tool" || e.Value != "classify" {
		t.Fatalf("unexpected type/value: %+v", e)
	}
	if len(e.Args) != 1 || e.Args[0] != "Login" {
		t.Fatalf("unexpected args: %+v", e.Args)
	}
}

func TestParseEnvelope_MarkdownFence_Tolerated(t *testing.T) {
	raw := "```json\n{\"type\":\"command\",\"value\":\"finalize\",\"args\":[\"Bug\"]}\n```"
	e := ParseEnvelope(raw)
	if e == nil || e.Value != "finalize" || e.Args[0] != "Bug" {
		t.Fatalf("unexpected result: %+v", e)
	}
}

func TestParseEnvelope_SurroundingText_ExtractsObject(t *testing.T) {
	raw := `Claro! Aqui está: {"type":"text","value":"start","args":[]} — espero ter ajudado.`
	e := ParseEnvelope(raw)
	if e == nil || e.Value != "start" {
		t.Fatalf("unexpected result: %+v", e)
	}
}

func TestParseEnvelope_NoArgs_ReturnsEmptyArray(t *testing.T) {
	e := ParseEnvelope(`{"type":"text","value":"start"}`)
	if e == nil || len(e.Args) != 0 {
		t.Fatalf("unexpected args: %+v", e)
	}
}

func TestParseEnvelope_IgnoresEmptyOrBlankArgs(t *testing.T) {
	e := ParseEnvelope(`{"type":"tool","value":"x","args":["a","","  ","b"]}`)
	if e == nil || len(e.Args) != 2 || e.Args[0] != "a" || e.Args[1] != "b" {
		t.Fatalf("unexpected args: %+v", e)
	}
}

func TestParseEnvelope_InvalidInput_ReturnsNil(t *testing.T) {
	cases := []string{
		"",
		"   ",
		`{ "type": "text", "value": `,
		"isso não é json",
		"[1,2,3]",
	}
	for _, raw := range cases {
		if ParseEnvelope(raw) != nil {
			t.Errorf("expected nil for %q", raw)
		}
	}
}

func TestEnvelope_ToJSON_RoundTrips(t *testing.T) {
	original := NewEnvelope(EnvelopeType.Command, "finalize", []string{"Épico"})
	roundtrip := ParseEnvelope(original.ToJSON())
	if roundtrip == nil || roundtrip.Type != original.Type || roundtrip.Value != original.Value {
		t.Fatalf("roundtrip mismatch: %+v vs %+v", original, roundtrip)
	}
	if len(roundtrip.Args) != 1 || roundtrip.Args[0] != "Épico" {
		t.Fatalf("unexpected args: %+v", roundtrip.Args)
	}
}

func TestParseEnvelope_WithContext_FillsMap(t *testing.T) {
	e := ParseEnvelope(`{"type":"text","value":"start","context":{"driver":"claude code"}}`)
	if e == nil || e.Context["driver"] != "claude code" {
		t.Fatalf("unexpected context: %+v", e)
	}
}

func TestParseEnvelope_WithoutContext_ReturnsNilMap(t *testing.T) {
	e := ParseEnvelope(`{"type":"text","value":"start"}`)
	if e == nil || e.Context != nil {
		t.Fatalf("expected nil context, got %+v", e)
	}
}

func TestEnvelope_ToJSON_WithContext_RoundTrips(t *testing.T) {
	original := NewEnvelope(EnvelopeType.Text, "start", nil)
	original.Context = map[string]string{"driver": "claude code"}

	roundtrip := ParseEnvelope(original.ToJSON())
	if roundtrip == nil || roundtrip.Context["driver"] != "claude code" {
		t.Fatalf("unexpected roundtrip: %+v", roundtrip)
	}
}

func TestEnvelope_ToJSON_WithoutContext_OmitsField(t *testing.T) {
	e := NewEnvelope(EnvelopeType.Command, "finalize", []string{"Épico"})
	if strings.Contains(e.ToJSON(), "context") {
		t.Fatalf("expected no context field in %s", e.ToJSON())
	}
}

func TestParseEnvelope_IgnoresUnknownFields(t *testing.T) {
	e := ParseEnvelope(`{"type":"tool","value":"classify","args":["x"],"tokens":1234}`)
	if e == nil || e.Value != "classify" {
		t.Fatalf("unexpected result: %+v", e)
	}
}
