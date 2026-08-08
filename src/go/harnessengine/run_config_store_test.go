package harnessengine

import (
	"os"
	"reflect"
	"testing"
)

func TestRunConfig_WriteAndLoad_RoundTrip(t *testing.T) {
	isolate(t)

	WriteRunConfig(RunConfig{VerifyCmd: "npm test", TargetDir: "app"})

	loaded := LoadRunConfig()
	if loaded.VerifyCmd != "npm test" || loaded.TargetDir != "app" {
		t.Fatalf("unexpected config: %+v", loaded)
	}
}

func TestRunConfig_WriteAndLoad_PreservesRunId(t *testing.T) {
	isolate(t)

	WriteRunConfig(RunConfig{VerifyCmd: "npm test", TargetDir: "app", RunId: "019b1ed0-6bea-7bc1-a790-0bdb42bb8ab6"})

	if got := LoadRunConfig().RunId; got != "019b1ed0-6bea-7bc1-a790-0bdb42bb8ab6" {
		t.Fatalf("unexpected run id: %s", got)
	}
}

func TestRunConfig_Load_MissingFile_ReturnsDefaults(t *testing.T) {
	isolate(t)

	loaded := LoadRunConfig()
	if loaded.VerifyCmd != "" || loaded.TargetDir != "." {
		t.Fatalf("unexpected defaults: %+v", loaded)
	}
}

func TestRunConfig_Reset_DeletesFile(t *testing.T) {
	isolate(t)

	WriteRunConfig(RunConfig{VerifyCmd: "npm test", TargetDir: "app"})
	ResetRunConfig()

	if got := LoadRunConfig(); !reflect.DeepEqual(got, DefaultRunConfig()) {
		t.Fatalf("expected defaults after reset, got %+v", got)
	}
}

func TestRunConfig_Reset_MissingFile_DoesNotPanic(t *testing.T) {
	isolate(t)

	ResetRunConfig()
}

func TestRunConfig_WriteAndLoad_RoundTrip_WithVerifyCmds(t *testing.T) {
	isolate(t)

	WriteRunConfig(RunConfig{
		VerifyCmd:  "npm test",
		TargetDir:  "app",
		VerifyCmds: []string{"npm run lint", "npm run typecheck"},
	})

	loaded := LoadRunConfig()
	want := []string{"npm run lint", "npm run typecheck"}
	if !reflect.DeepEqual(loaded.VerifyCmds, want) {
		t.Fatalf("unexpected VerifyCmds: %+v", loaded.VerifyCmds)
	}
}

// TestRunConfig_Load_LegacyJSONWithoutVerifyCmds_ReturnsNilVerifyCmds proves a
// run_config.json written before VerifyCmds existed (no "verifyCmds" key at all) loads
// cleanly: encoding/json leaves the field at its zero value (nil), which is the signal the
// automated-verify path uses to fall back to the single VerifyCmd — see the doc comment on
// RunConfig.VerifyCmds. Mirrors TestFeatures_LoadLegacyListWithoutDependsOn_DoesNotPanic's
// "write raw old-format JSON, load, assert" idiom.
func TestRunConfig_Load_LegacyJSONWithoutVerifyCmds_ReturnsNilVerifyCmds(t *testing.T) {
	isolate(t)

	if err := ensureDir(".harness"); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(runConfigFilePath, []byte(`{"verifyCmd":"npm test","targetDir":"app","runId":"r1"}`), 0o644); err != nil {
		t.Fatal(err)
	}

	loaded := LoadRunConfig()
	if loaded.VerifyCmds != nil {
		t.Fatalf("expected nil VerifyCmds for legacy JSON, got %+v", loaded.VerifyCmds)
	}
	if loaded.VerifyCmd != "npm test" || loaded.TargetDir != "app" || loaded.RunId != "r1" {
		t.Fatalf("unexpected legacy fields: %+v", loaded)
	}
}
