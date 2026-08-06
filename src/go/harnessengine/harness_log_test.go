package harnessengine

import (
	"os"
	"strings"
	"testing"
)

func TestLogInfo_WritesOneLineWithLevelAndMessage(t *testing.T) {
	isolate(t)

	LogInfo("[step 1] enter 'start'")

	data, err := os.ReadFile(harnessLogFilePath)
	if err != nil {
		t.Fatalf("expected harness.log to exist: %v", err)
	}
	line := strings.TrimSpace(string(data))
	if !strings.Contains(line, "[INFO]") || !strings.Contains(line, "[step 1] enter 'start'") {
		t.Fatalf("unexpected line: %s", line)
	}
}

func TestLogError_WritesToFile(t *testing.T) {
	isolate(t)

	LogError("[harness] something failed")

	data, err := os.ReadFile(harnessLogFilePath)
	if err != nil {
		t.Fatalf("expected harness.log to exist: %v", err)
	}
	line := strings.TrimSpace(string(data))
	if !strings.Contains(line, "[ERROR]") || !strings.Contains(line, "[harness] something failed") {
		t.Fatalf("unexpected line: %s", line)
	}
}

func TestResetHarnessLog_RemovesFile(t *testing.T) {
	isolate(t)

	LogInfo("first run")
	if !fileExists(harnessLogFilePath) {
		t.Fatal("expected harness.log to exist before reset")
	}

	ResetHarnessLog()

	if fileExists(harnessLogFilePath) {
		t.Fatal("expected harness.log to be removed after reset")
	}
}

func TestResetHarnessLog_NoFileYet_DoesNotPanic(t *testing.T) {
	isolate(t)

	ResetHarnessLog()
}
