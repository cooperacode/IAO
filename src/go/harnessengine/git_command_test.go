package harnessengine

import (
	"strings"
	"testing"
)

func TestGitCommand_Run_ValidCommand_CapturesStdout(t *testing.T) {
	dir := t.TempDir()

	result := RunGitCommand(dir, "--version")

	if result.ExitCode != 0 {
		t.Fatalf("unexpected exit code: %d (%s)", result.ExitCode, result.Error)
	}
	if !strings.Contains(result.Output, "git version") {
		t.Fatalf("unexpected output: %s", result.Output)
	}
}

func TestGitCommand_Run_NonExistentDirectory_ReturnsErrorWithoutPanicking(t *testing.T) {
	dir := t.TempDir()
	missing := dir + "/missing"

	result := RunGitCommand(missing, "status")

	if result.ExitCode == 0 {
		t.Fatal("expected non-zero exit code")
	}
	if result.Error == "" {
		t.Fatal("expected an error message")
	}
}

func TestGitCommand_Run_InjectsHooksAndPagerIsolation(t *testing.T) {
	dir := t.TempDir()

	hooksPath := RunGitCommand(dir, "config", "--get", "core.hooksPath")
	if hooksPath.ExitCode != 0 {
		t.Fatalf("unexpected exit code: %d", hooksPath.ExitCode)
	}
	if !strings.HasSuffix(strings.TrimSpace(hooksPath.Output), "iao-no-hooks") {
		t.Fatalf("unexpected hooks path: %s", hooksPath.Output)
	}

	pager := RunGitCommand(dir, "config", "--get", "core.pager")
	if pager.ExitCode != 0 || strings.TrimSpace(pager.Output) != "cat" {
		t.Fatalf("unexpected pager config: %+v", pager)
	}
}
