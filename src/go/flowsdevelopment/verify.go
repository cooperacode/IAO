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
		engine.LogError(fmt.Sprintf("[dev] invalid target_dir for automatic verify: %s", err))
		return missingVerify()
	}

	script := filepath.Join(targetDir, "verify-feature.sh")
	isScript := fileExistsLocal(script)

	// verify-feature.sh still wins over everything else (unchanged precedent). Below that,
	// a configured list of commands (VerifyCmds) takes priority over the single configured
	// VerifyCmd — a non-empty list is a strictly more specific choice by whoever ran `plan`.
	if !isScript {
		if commands := engine.LoadRunConfig().VerifyCmds; len(commands) > 0 {
			return tryParallelConfiguredVerify(targetDir, commands, featureId)
		}
	}

	command := []string{"bash", script, strconv.Itoa(featureId)}
	label := fmt.Sprintf("bash ./verify-feature.sh %d", featureId)
	if !isScript {
		command = configuredVerifyArgv(engine.LoadRunConfig().VerifyCmd)
		if len(command) == 0 {
			return missingVerify()
		}
		label = strings.Join(command, " ")
	}

	result := runVerifyScript(targetDir, command)
	logPath := writeVerifyLog(targetDir, label, featureId, result)
	if result.TimedOut {
		return failedVerify(fmt.Sprintf("FAIL: verification exceeded timeout (%s)%s",
			verifyTimeoutDescription(), verifyOutputSuffix(result, logPath)))
	}

	if result.ExitCode == 0 {
		kind := "configured verify command"
		if isScript {
			kind = fmt.Sprintf("verify-feature.sh %d", featureId)
		}
		return passedVerify(fmt.Sprintf("PASS: %s passed%s", kind, logSuffix(logPath)))
	}

	return failedVerify(fmt.Sprintf("FAIL: verification failed (exit %d)%s",
		result.ExitCode, verifyOutputSuffix(result, logPath)))
}

type verifyScriptResult struct {
	ExitCode int
	Output   string
	Error    string
	TimedOut bool
}

func runVerifyScript(targetDir string, command []string) verifyScriptResult {
	cmd := exec.Command(command[0], command[1:]...)
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

// parallelVerifyOutcome is one command's contribution to the aggregate verdict computed by
// tryParallelConfiguredVerify — carried back over a channel, keyed by its original 0-based
// position so results can be reassembled in the order the commands were configured in,
// regardless of which goroutine finishes first.
type parallelVerifyOutcome struct {
	index  int
	passed bool
	line   string
}

// tryParallelConfiguredVerify runs every entry of RunConfig.VerifyCmds CONCURRENTLY and
// combines them with AND logic: the feature only passes if every command exits 0. Each
// command gets its own log file (verify-feature-{featureId}-{n}.log, 1-based) so concurrent
// runs never race on the same file — see writeVerifyLogIndexed.
//
// Tokenization (configuredVerifyArgv, the same allowlist the single-VerifyCmd path already
// uses) happens for ALL commands up front, before any subprocess is launched: since the
// verdict is AND, a single disallowed command already dooms the whole check, so there is no
// reason to pay for concurrent subprocesses whose result cannot change the outcome.
func tryParallelConfiguredVerify(targetDir string, commands []string, featureId int) automatedVerifyResult {
	argvs := make([][]string, len(commands))
	for i, raw := range commands {
		argv := configuredVerifyArgv(raw)
		if len(argv) == 0 {
			return failedVerify(fmt.Sprintf(
				"FAIL: verify command #%d is empty or uses disallowed shell operators: %s", i+1, raw))
		}
		argvs[i] = argv
	}

	// One goroutine per command, same idiom as runVerifyScript's own timeout race and
	// TaskRegistry.runWithTimeout: goroutine + buffered channel, no third-party library.
	// The channel is sized to the command count so no goroutine blocks trying to send.
	outcomes := make(chan parallelVerifyOutcome, len(commands))
	for i, argv := range argvs {
		go func(index int, argv []string, raw string) {
			outcomes <- runOneConfiguredVerify(targetDir, argv, raw, featureId, index)
		}(i, argv, commands[i])
	}

	ordered := make([]parallelVerifyOutcome, len(commands))
	for range argvs {
		o := <-outcomes
		ordered[o.index] = o
	}

	lines := make([]string, len(ordered))
	failures := 0
	for i, o := range ordered {
		lines[i] = o.line
		if !o.passed {
			failures++
		}
	}
	joined := strings.Join(lines, " | ")

	if failures == 0 {
		return passedVerify(fmt.Sprintf("PASS: all %d verify commands passed. %s", len(commands), joined))
	}
	return failedVerify(fmt.Sprintf("FAIL: %d of %d verify commands did not pass. %s", failures, len(commands), joined))
}

// runOneConfiguredVerify runs a single already-tokenized verify command (index is 0-based;
// display uses the 1-based n) and formats its line for the aggregate message, reusing the
// same suffix helpers (logSuffix, verifyOutputSuffix, firstMeaningfulLine/snippet) the
// single-command path already relies on.
func runOneConfiguredVerify(targetDir string, argv []string, raw string, featureId, index int) parallelVerifyOutcome {
	n := index + 1
	result := runVerifyScript(targetDir, argv)
	logPath := writeVerifyLogIndexed(targetDir, raw, featureId, n, result)

	if result.TimedOut {
		return parallelVerifyOutcome{
			index: index,
			line:  fmt.Sprintf("#%d TIMEOUT (%s): %s%s", n, verifyTimeoutDescription(), raw, logSuffix(logPath)),
		}
	}
	if result.ExitCode == 0 {
		return parallelVerifyOutcome{
			index:  index,
			passed: true,
			line:   fmt.Sprintf("#%d PASS: %s%s", n, raw, logSuffix(logPath)),
		}
	}
	return parallelVerifyOutcome{
		index: index,
		line:  fmt.Sprintf("#%d FAIL (exit %d): %s%s", n, result.ExitCode, raw, verifyOutputSuffix(result, logPath)),
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

// writeVerifyLog writes the single-command verify log — unchanged behavior, now a thin
// wrapper over writeVerifyLogIndexed with no check index (Go has no default parameters).
func writeVerifyLog(targetDir, command string, featureId int, result verifyScriptResult) string {
	return writeVerifyLogIndexed(targetDir, command, featureId, 0, result)
}

// writeVerifyLogIndexed writes one command's verify log. checkIndex is 1-based; 0 means
// "the only command" and keeps the original, un-suffixed path
// (.harness/logs/verify-feature-{featureId}.log) so single-VerifyCmd behavior and any
// external tooling that reads that path are untouched. A positive checkIndex (one of several
// concurrently run VerifyCmds) gets its own file, .harness/logs/verify-feature-{featureId}-{n}.log,
// so concurrent commands never race on the same log.
func writeVerifyLogIndexed(targetDir, command string, featureId, checkIndex int, result verifyScriptResult) string {
	fileName := fmt.Sprintf("verify-feature-%d.log", featureId)
	if checkIndex > 0 {
		fileName = fmt.Sprintf("verify-feature-%d-%d.log", featureId, checkIndex)
	}
	relativePath := filepath.Join(".harness", "logs", fileName)
	displayPath := strings.ReplaceAll(relativePath, "\\", "/")

	fullPath := relativePath
	if err := os.MkdirAll(filepath.Dir(fullPath), 0o755); err != nil {
		return fmt.Sprintf("log unavailable (%s)", oneLine(err.Error(), ""))
	}

	content := fmt.Sprintf(
		"timestampUtc: %s\ncommand: %s\ncwd: %s\nexitCode: %d\ntimedOut: %t\n\n--- stdout ---\n%s\n\n--- stderr ---\n%s",
		time.Now().UTC().Format("2006-01-02T15:04:05.0000000Z"), command, targetDir,
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

func configuredVerifyArgv(raw string) []string {
	text := strings.TrimSpace(raw)
	if text == "" || strings.ContainsAny(text, ";&|<>`$") {
		return nil
	}
	args := strings.Fields(text)
	if len(args) == 0 {
		return nil
	}
	bin := strings.ToLower(filepath.Base(args[0]))
	if bin == "sh" || bin == "bash" || bin == "zsh" || bin == "fish" || bin == "cmd" || bin == "powershell" || bin == "pwsh" {
		for _, arg := range args[1:] {
			if arg == "-c" || arg == "-command" || arg == "/c" {
				return nil
			}
		}
	}
	return args
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
