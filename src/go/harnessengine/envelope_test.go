package harnessengine

import (
	"os"
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
	raw := `Sure! Here it is: {"type":"text","value":"start","args":[]} — hope that helps.`
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
	isolate(t)

	cases := []string{
		"",
		"   ",
		`{ "type": "text", "value": `,
		"this is not json",
		"[1,2,3]",
	}
	for _, raw := range cases {
		if ParseEnvelope(raw) != nil {
			t.Errorf("expected nil for %q", raw)
		}
	}
}

func TestParseEnvelope_InvalidInput_WritesRawPayloadToHarnessLog(t *testing.T) {
	isolate(t)

	// The raw driver payload is otherwise lost forever — the inbox file gets overwritten
	// by the next attempt before anyone can inspect what actually failed.
	ParseEnvelope("this is not json")

	data, err := os.ReadFile(harnessLogFilePath)
	if err != nil {
		t.Fatalf("expected harness.log to exist: %v", err)
	}
	if !strings.Contains(string(data), "this is not json") {
		t.Fatalf("expected raw payload in harness.log, got: %s", data)
	}
}

func TestParseEnvelope_OversizedPayload_TruncatedInHarnessLog(t *testing.T) {
	isolate(t)

	oversized := strings.Repeat("x", 600) + "not json"
	ParseEnvelope(oversized)

	data, err := os.ReadFile(harnessLogFilePath)
	if err != nil {
		t.Fatalf("expected harness.log to exist: %v", err)
	}
	content := string(data)
	if !strings.Contains(content, "...(truncated)") {
		t.Fatalf("expected truncation marker, got: %s", content)
	}
	if strings.Contains(content, oversized) {
		t.Fatalf("expected payload to be truncated, got the full string in: %s", content)
	}
}

func TestEnvelope_ToJSON_RoundTrips(t *testing.T) {
	original := NewEnvelope(EnvelopeType.Command, "finalize", []string{"Epic"})
	roundtrip := ParseEnvelope(original.ToJSON())
	if roundtrip == nil || roundtrip.Type != original.Type || roundtrip.Value != original.Value {
		t.Fatalf("roundtrip mismatch: %+v vs %+v", original, roundtrip)
	}
	if len(roundtrip.Args) != 1 || roundtrip.Args[0] != "Epic" {
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
	e := NewEnvelope(EnvelopeType.Command, "finalize", []string{"Epic"})
	if strings.Contains(e.ToJSON(), "context") {
		t.Fatalf("expected no context field in %s", e.ToJSON())
	}
}

func TestEnvelope_ContextUsage_RoundTrips(t *testing.T) {
	original := NewEnvelope(EnvelopeType.Command, "start", nil)
	original.ContextUsage = &ContextUsage{
		Schema: "iao.context.v1", SessionID: "s1", ContextWindowTokens: 128000,
		ContextUsedTokens: 84000, Source: "driver",
	}

	roundtrip := ParseEnvelope(original.ToJSON())
	if roundtrip == nil || roundtrip.ContextUsage == nil || *roundtrip.ContextUsage != *original.ContextUsage {
		t.Fatalf("unexpected context usage roundtrip: %+v", roundtrip)
	}
}

func TestParseEnvelope_IgnoresUnknownFields(t *testing.T) {
	e := ParseEnvelope(`{"type":"tool","value":"classify","args":["x"],"tokens":1234}`)
	if e == nil || e.Value != "classify" {
		t.Fatalf("unexpected result: %+v", e)
	}
}
