package harnessengine

import (
	"bytes"
	"os"
	"os/exec"
	"path/filepath"
	"sync"
)

// GitCommandResult is the outcome of a git invocation.
type GitCommandResult struct {
	ExitCode int
	Output   string
	Error    string
}

var (
	noHooksDirOnce sync.Once
	noHooksDirPath string
)

// noHooksDir returns (creating if needed) a stable, always-empty directory used as
// core.hooksPath — neutralizes any local or global hook of the target repo (RFC §6.11).
// Created once, idempotently; never receives scripts.
func noHooksDir() string {
	noHooksDirOnce.Do(func() {
		dir := filepath.Join(os.TempDir(), "iao-no-hooks")
		_ = os.MkdirAll(dir, 0o755)
		noHooksDirPath = dir
	})
	return noHooksDirPath
}

// RunGitCommand runs git in workingDirectory. The engine provides the mechanism; flows
// decide which commands to run and how to interpret the result.
func RunGitCommand(workingDirectory string, args ...string) GitCommandResult {
	// Git isolation (RFC §6.11): ahead of the caller's args, always. Neutralizes hooks
	// (core.hooksPath pointed at an empty dir), the credential helper (avoids a prompt or a
	// stored-credential leak), and the pager (core.pager=cat avoids hanging on an
	// interactive subprocess waiting for stdin that never arrives).
	fullArgs := []string{
		"-c", "core.hooksPath=" + noHooksDir(),
		"-c", "credential.helper=",
		"-c", "core.pager=cat",
	}
	fullArgs = append(fullArgs, args...)

	cmd := exec.Command("git", fullArgs...)
	cmd.Dir = workingDirectory

	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	if err := cmd.Run(); err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			return GitCommandResult{ExitCode: exitErr.ExitCode(), Output: stdout.String(), Error: stderr.String()}
		}
		return GitCommandResult{ExitCode: -1, Output: "", Error: err.Error()}
	}

	return GitCommandResult{ExitCode: 0, Output: stdout.String(), Error: stderr.String()}
}
