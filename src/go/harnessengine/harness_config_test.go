package harnessengine

import (
	"os"
	"testing"
)

func TestConfig_Load_MissingFile_UsesDefaults(t *testing.T) {
	isolate(t)

	config := LoadConfig()

	if config != DefaultHarnessConfig() {
		t.Fatalf("unexpected config: %+v", config)
	}
	if config.MaxSteps != 12 || config.MaxInstructionChars != 0 || config.TimeoutMs != 10*60_000 {
		t.Fatalf("unexpected config: %+v", config)
	}
}

func TestConfig_Load_WithTimeout_ReadsAndNormalizes(t *testing.T) {
	isolate(t)

	os.WriteFile("harness.json", []byte(`{"timeoutMs":30000}`), 0o644)
	if got := LoadConfig().TimeoutMs; got != 30000 {
		t.Fatalf("unexpected timeout: %d", got)
	}

	// Negative value falls back to the enabled default; timeout cannot be disabled.
	os.WriteFile("harness.json", []byte(`{"timeoutMs":-5}`), 0o644)
	if got := LoadConfig().TimeoutMs; got != 10*60_000 {
		t.Fatalf("unexpected timeout: %d", got)
	}
}

func TestConfig_Load_WithFile_UsesFileValues(t *testing.T) {
	isolate(t)

	os.WriteFile("harness.json", []byte(`{"maxSteps":5,"maxInstructionChars":20000,"docsMaxChars":10000,"docsFolder":"specs"}`), 0o644)

	config := LoadConfig()
	if config.MaxSteps != 5 || config.MaxInstructionChars != 20000 || config.DocsMaxChars != 10000 || config.DocsFolder != "specs" {
		t.Fatalf("unexpected config: %+v", config)
	}
}

func TestConfig_Load_PartialFile_FillsWithDefaults(t *testing.T) {
	isolate(t)

	os.WriteFile("harness.json", []byte(`{"maxInstructionChars":8000}`), 0o644)

	config := LoadConfig()
	def := DefaultHarnessConfig()
	if config.MaxInstructionChars != 8000 || config.MaxSteps != def.MaxSteps ||
		config.DocsMaxChars != def.DocsMaxChars || config.DocsFolder != def.DocsFolder {
		t.Fatalf("unexpected config: %+v", config)
	}
}

func TestConfig_Load_InvalidFile_FallsBackToDefaultsWithoutPanicking(t *testing.T) {
	isolate(t)

	os.WriteFile("harness.json", []byte("{ this is not json "), 0o644)

	if got := LoadConfig(); got != DefaultHarnessConfig() {
		t.Fatalf("unexpected config: %+v", got)
	}
}

func TestConfig_Load_TimeoutAboveCeiling_ClampsToMaximum(t *testing.T) {
	isolate(t)

	os.WriteFile("harness.json", []byte(`{"timeoutMs":99999999}`), 0o644)

	if got := LoadConfig().TimeoutMs; got != 10*60_000 {
		t.Fatalf("unexpected timeout: %d", got)
	}
}

func TestConfig_Load_EnvVar_OverridesFileTimeout(t *testing.T) {
	isolate(t)

	os.WriteFile("harness.json", []byte(`{"timeoutMs":1000}`), 0o644)
	t.Setenv("HARNESS_TIMEOUT_MS", "2000")

	if got := LoadConfig().TimeoutMs; got != 2000 {
		t.Fatalf("unexpected timeout: %d", got)
	}
}

func TestConfig_Load_EnvVar_AlsoRespectsCeiling(t *testing.T) {
	isolate(t)

	t.Setenv("HARNESS_TIMEOUT_MS", "99999999")

	if got := LoadConfig().TimeoutMs; got != 10*60_000 {
		t.Fatalf("unexpected timeout: %d", got)
	}
}

func TestConfig_Load_InvalidEnvVar_IsIgnored(t *testing.T) {
	isolate(t)

	os.WriteFile("harness.json", []byte(`{"timeoutMs":1000}`), 0o644)
	t.Setenv("HARNESS_TIMEOUT_MS", "not a number")

	if got := LoadConfig().TimeoutMs; got != 1000 {
		t.Fatalf("unexpected timeout: %d", got)
	}
}
