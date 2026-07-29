package main

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

type automatedVerifyResult struct {
	Attempted bool
	Success   bool
	Result    string
}

func missingVerify() automatedVerifyResult { return automatedVerifyResult{} }
func passedVerify(result string) automatedVerifyResult {
	return automatedVerifyResult{Attempted: true, Success: true, Result: result}
}
func failedVerify(result string) automatedVerifyResult {
	return automatedVerifyResult{Attempted: true, Success: false, Result: result}
}

func tryAutomatedVerify() automatedVerifyResult {
	featureId, err := strconv.Atoi(state(currentFeatureIdKey))
	if err != nil {
		return missingVerify()
	}

	targetDir, err := resolveTargetDir(engine.LoadRunConfig().TargetDir)
	if err != nil {
		// Invalid target_dir (root, home, harness install) -> same "didn't attempt
		// automated verification" path as a target_dir with no verify-feature.sh; doesn't
		// bring the process down with an unhandled error.
		fmt.Fprintf(os.Stderr, "[dev] invalid target_dir for automatic verify: %s\n", err)
		return missingVerify()
	}

	script := filepath.Join(targetDir, "verify-feature.sh")
	if !fileExistsLocal(script) {
		return missingVerify()
	}

	result := runVerifyScript(targetDir, script, featureId)
	logPath := writeVerifyLog(targetDir, script, featureId, result)
	if result.TimedOut {
		return failedVerify(fmt.Sprintf("FAIL: verify-feature.sh %d exceeded timeout (%s)%s",
			featureId, verifyTimeoutDescription(), verifyOutputSuffix(result, logPath)))
	}

	if result.ExitCode == 0 {
		return passedVerify(passResult(featureId, result.Output, result.Error, logPath))
	}

	return failedVerify(fmt.Sprintf("FAIL: verify-feature.sh %d failed (exit %d)%s",
		featureId, result.ExitCode, verifyOutputSuffix(result, logPath)))
}

type verifyScriptResult struct {
	ExitCode int
	Output   string
	Error    string
	TimedOut bool
}

func runVerifyScript(targetDir, script string, featureId int) verifyScriptResult {
	cmd := exec.Command("bash", script, strconv.Itoa(featureId))
	cmd.Dir = targetDir
	// New process group so a timeout can kill the whole tree, not just the direct child.
	// Implemented per-OS in process_unix.go/process_windows.go.
	setProcessGroup(cmd)

	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	if err := cmd.Start(); err != nil {
		return verifyScriptResult{ExitCode: -1, Error: err.Error()}
	}

	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()

	timeoutMs := verifyTimeoutMs()
	if timeoutMs <= 0 {
		err := <-done
		return verifyScriptResult{ExitCode: exitCodeOf(err), Output: stdout.String(), Error: stderr.String()}
	}

	select {
	case err := <-done:
		return verifyScriptResult{ExitCode: exitCodeOf(err), Output: stdout.String(), Error: stderr.String()}
	case <-time.After(time.Duration(timeoutMs) * time.Millisecond):
		// Kill the whole process group; the script may have spawned children.
		killProcessTree(cmd)
		<-done
		return verifyScriptResult{ExitCode: -1, Output: stdout.String(), Error: stderr.String(), TimedOut: true}
	}
}

func exitCodeOf(err error) int {
	if err == nil {
		return 0
	}
	if exitErr, ok := err.(interface{ ExitCode() int }); ok {
		return exitErr.ExitCode()
	}
	return -1
}

func verifyTimeoutMs() int {
	timeoutMs := engine.CurrentConfig().TimeoutMs
	if timeoutMs <= 0 {
		return 0
	}
	margin := min(500, max(1, timeoutMs/10))
	return max(1, timeoutMs-margin)
}

func verifyTimeoutDescription() string {
	timeoutMs := verifyTimeoutMs()
	if timeoutMs <= 0 {
		return "no limit"
	}
	return fmt.Sprintf("%dms", timeoutMs)
}

func writeVerifyLog(targetDir, script string, featureId int, result verifyScriptResult) string {
	relativePath := filepath.Join(".harness", "logs", fmt.Sprintf("verify-feature-%d.log", featureId))
	displayPath := strings.ReplaceAll(relativePath, "\\", "/")

	fullPath, err := filepath.Abs(relativePath)
	if err != nil {
		return fmt.Sprintf("log unavailable (%s)", oneLine(err.Error(), ""))
	}
	if err := os.MkdirAll(filepath.Dir(fullPath), 0o755); err != nil {
		return fmt.Sprintf("log unavailable (%s)", oneLine(err.Error(), ""))
	}

	content := fmt.Sprintf(
		"timestampUtc: %s\ncommand: bash ./verify-feature.sh %d\ncwd: %s\nscript: %s\nexitCode: %d\ntimedOut: %t\n\n--- stdout ---\n%s\n\n--- stderr ---\n%s",
		time.Now().UTC().Format("2006-01-02T15:04:05.0000000Z"), featureId, targetDir, script,
		result.ExitCode, result.TimedOut, result.Output, result.Error)

	if err := os.WriteFile(fullPath, []byte(content), 0o644); err != nil {
		return fmt.Sprintf("log unavailable (%s)", oneLine(err.Error(), ""))
	}

	return displayPath
}

func passResult(featureId int, output, errOutput, logPath string) string {
	firstLine := firstMeaningfulLine(output, errOutput)
	var result string
	if strings.HasPrefix(strings.ToUpper(firstLine), "PASS") {
		result = snippet(firstLine, 240)
	} else {
		result = fmt.Sprintf("PASS: verify-feature.sh %d passed", featureId)
	}
	return result + logSuffix(logPath)
}

func verifyOutputSuffix(result verifyScriptResult, logPath string) string {
	output := snippet(firstMeaningfulLine(result.Output, result.Error), 240)
	if strings.TrimSpace(output) == "" {
		return logSuffix(logPath)
	}
	return fmt.Sprintf(": %s%s", output, logSuffix(logPath))
}

func firstMeaningfulLine(values ...string) string {
	for _, value := range values {
		normalized := strings.ReplaceAll(value, "\r", "\n")
		for _, line := range strings.Split(normalized, "\n") {
			line = strings.TrimSpace(line)
			if line != "" {
				return line
			}
		}
	}
	return ""
}

func logSuffix(logPath string) string {
	if strings.TrimSpace(logPath) == "" {
		return ""
	}
	return fmt.Sprintf(". Log: %s", logPath)
}

func snippet(value string, maxChars int) string {
	text := oneLine(value, "")
	if len(text) <= maxChars {
		return text
	}
	return strings.TrimRight(truncateUtf8Bytes(text, maxChars), " \t") + "..."
}

func fileExistsLocal(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func max(a, b int) int {
	if a > b {
		return a
	}
	return b
}
