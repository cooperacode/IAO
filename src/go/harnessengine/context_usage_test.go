package harnessengine

import (
	"os"
	"testing"
)

func TestContextPolicy_UsesTelemetryThenFallback(t *testing.T) {
	isolate(t)
	if err := os.WriteFile("harness.json", []byte(`{"contextResetMode":"adaptive","contextResetThreshold":0.7,"contextFallbackFeatures":1}`), 0o644); err != nil {
		t.Fatal(err)
	}
	ReloadConfig()
	ResetState()

	if got := NewFeaturePrefix(); got == "" {
		t.Fatal("first feature must establish a boundary")
	}
	ObserveContextUsage(&ContextUsage{ContextWindowTokens: 100, ContextUsedTokens: 50})
	if got := NewFeaturePrefix(); got != "" {
		t.Fatalf("expected reuse below threshold, got %q", got)
	}
	ObserveContextUsage(&ContextUsage{ContextWindowTokens: 100, ContextUsedTokens: 80})
	if got := NewFeaturePrefix(); got == "" {
		t.Fatal("expected reset at the adaptive threshold")
	}
}

func TestContextUsage_FromEnvironment(t *testing.T) {
	t.Setenv("HARNESS_CONTEXT_USAGE_JSON", `{"contextWindowTokens":100,"contextUsedTokens":70,"source":"host"}`)
	usage := ContextUsageFromEnvironment()
	if usage == nil || usage.ContextWindowTokens != 100 || usage.ContextUsedTokens != 70 {
		t.Fatalf("unexpected environment usage: %+v", usage)
	}
}
