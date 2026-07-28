package harnessengine

import (
	"strings"
	"testing"
)

func TestSkills_MultipleNames_ReturnsAllMappings(t *testing.T) {
	m := Skills("agile-workitem", "story-splitting")

	if len(m) != 2 {
		t.Fatalf("unexpected map: %+v", m)
	}
	if m["agile-workitem"] != "skills/agile-workitem/SKILL.md" {
		t.Fatalf("unexpected mapping: %s", m["agile-workitem"])
	}
	if m["story-splitting"] != "skills/story-splitting/SKILL.md" {
		t.Fatalf("unexpected mapping: %s", m["story-splitting"])
	}
}

func TestFormat_PersistedContext_ReinjectedIntoOutputEnvelope(t *testing.T) {
	isolate(t)

	SetContext(map[string]string{"driver": "claude code"})
	output := NewEnvelope(EnvelopeType.Command, "plan", nil)

	result := Format("faça algo", output, nil)

	if !strings.Contains(result, `"context":{"driver":"claude code"}`) {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestFormat_NoPersistedContext_OmitsField(t *testing.T) {
	isolate(t)

	output := NewEnvelope(EnvelopeType.Command, "plan", nil)

	result := Format("faça algo", output, nil)

	if strings.Contains(result, "context") {
		t.Fatalf("unexpected context in result: %s", result)
	}
}

func TestFormat_ContextAlreadySetByTask_IsNotOverwritten(t *testing.T) {
	isolate(t)

	SetContext(map[string]string{"driver": "claude code"})
	output := NewEnvelope(EnvelopeType.Command, "plan", nil)
	output.Context = map[string]string{"driver": "explicito"}

	result := Format("faça algo", output, nil)

	if !strings.Contains(result, "explicito") || strings.Contains(result, "claude code") {
		t.Fatalf("unexpected result: %s", result)
	}
}
