package harnessengine

import "testing"

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

	if got := LoadRunConfig(); got != DefaultRunConfig() {
		t.Fatalf("expected defaults after reset, got %+v", got)
	}
}

func TestRunConfig_Reset_MissingFile_DoesNotPanic(t *testing.T) {
	isolate(t)

	ResetRunConfig()
}
