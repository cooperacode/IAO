package main

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

type handoffResult struct {
	Success      bool
	Confirmation string
	Failure      string
}

func okHandoff(confirmation string) handoffResult {
	return handoffResult{Success: true, Confirmation: confirmation}
}
func failedHandoff(failure string) handoffResult { return handoffResult{Failure: failure} }

func completeVerifiedFeature(verifyResult string) string {
	handoff := tryAutomatedHandoff(verifyResult)
	if !handoff.Success {
		fmt.Fprintf(os.Stderr, "[dev] handoff automatico falhou: %s\n", handoff.Failure)
		return HandoffPrompt(handoff.Failure)
	}

	fmt.Fprintf(os.Stderr, "[dev] handoff automatico concluido: %s\n", handoff.Confirmation)
	if id, err := strconv.Atoi(state(currentFeatureIdKey)); err == nil {
		engine.MarkFeaturePassed(id)
	}

	if engine.AllFeaturesPassing() {
		return done()
	}
	return BearingsPrompt()
}

func tryAutomatedHandoff(verifyResult string) handoffResult {
	featureId, err := strconv.Atoi(state(currentFeatureIdKey))
	if err != nil {
		return failedHandoff("feature atual ausente no state.json")
	}

	title := state(currentFeatureTitleKey)
	for _, f := range engine.LoadFeatures() {
		if f.Id == featureId {
			title = f.Title
			break
		}
	}
	if strings.TrimSpace(title) == "" {
		title = fmt.Sprintf("feature #%d", featureId)
	}

	config := engine.LoadRunConfig()
	targetDir, err := resolveTargetDir(config.TargetDir)
	if err != nil {
		return failedHandoff(fmt.Sprintf("target_dir invalido: %s", err))
	}

	if err := os.MkdirAll(targetDir, 0o755); err != nil {
		return failedHandoff(fmt.Sprintf("falha ao atualizar progress.txt: %s", err))
	}
	if err := appendProgress(targetDir, featureId, title, config.VerifyCmd, verifyResult); err != nil {
		return failedHandoff(fmt.Sprintf("falha ao atualizar progress.txt: %s", err))
	}

	revParse := engine.RunGitCommand(targetDir, "rev-parse", "--show-toplevel")
	if revParse.ExitCode != 0 {
		return okHandoff(fmt.Sprintf("NO_GIT: %s", oneLine(revParse.Error, "diretorio-alvo fora de um repositorio Git")))
	}

	add := engine.RunGitCommand(targetDir, "add", "-A", "--", ".", ":(exclude).harness")
	if add.ExitCode != 0 {
		return failedHandoff(fmt.Sprintf("git add falhou: %s", oneLine(add.Error, add.Output)))
	}

	diff := engine.RunGitCommand(targetDir, "diff", "--cached", "--quiet", "--", ".", ":(exclude).harness")
	if diff.ExitCode == 0 {
		head := engine.RunGitCommand(targetDir, "rev-parse", "--short", "HEAD")
		if head.ExitCode == 0 {
			return okHandoff(oneLine(head.Output, "NO_CHANGES"))
		}
		return okHandoff("NO_CHANGES")
	}
	if diff.ExitCode > 1 {
		return failedHandoff(fmt.Sprintf("git diff --cached falhou: %s", oneLine(diff.Error, diff.Output)))
	}

	commit := engine.RunGitCommand(targetDir, "commit", "-m", commitMessage(featureId, title), "--", ".", ":(exclude).harness")
	if commit.ExitCode != 0 {
		return failedHandoff(fmt.Sprintf("git commit falhou: %s", oneLine(commit.Error, commit.Output)))
	}

	status := engine.RunGitCommand(targetDir, "status", "--short", "--", ".", ":(exclude).harness")
	if status.ExitCode != 0 {
		return failedHandoff(fmt.Sprintf("git status falhou: %s", oneLine(status.Error, status.Output)))
	}
	if strings.TrimSpace(status.Output) != "" {
		return failedHandoff(fmt.Sprintf("diretorio-alvo ainda sujo apos commit: %s", oneLine(status.Output, "")))
	}

	hash := engine.RunGitCommand(targetDir, "rev-parse", "--short", "HEAD")
	if hash.ExitCode == 0 {
		return okHandoff(oneLine(hash.Output, "COMMIT_CREATED"))
	}
	return failedHandoff(fmt.Sprintf("commit criado, mas hash nao foi lido: %s", oneLine(hash.Error, hash.Output)))
}

// resolveTargetDir applies the minimal containment (RFC §6.3): rejects targets that should
// certainly never receive automatic commits from the agent — empty, filesystem root, the
// user's HOME, or the harness's own install directory (using the running binary's
// directory as a proxy). Full containment against a signed policy root is future-phase work
// (capability broker); this is just the RFC's minimal rejection list.
func resolveTargetDir(targetDir string) (string, error) {
	if strings.TrimSpace(targetDir) == "" {
		return "", fmt.Errorf("target_dir vazio/whitespace nao e um diretorio-alvo valido.")
	}

	resolved, err := filepath.Abs(targetDir)
	if err != nil {
		return "", fmt.Errorf("target_dir invalido: %s", err)
	}

	caseInsensitive := runtime.GOOS == "windows"

	root := filepath.VolumeName(resolved) + string(filepath.Separator)
	if pathsEqual(resolved, root, caseInsensitive) {
		return "", fmt.Errorf("target_dir resolve para a raiz do sistema de arquivos ('%s').", resolved)
	}

	if home, err := os.UserHomeDir(); err == nil {
		if normalizedHome := normalizedOrEmpty(home); normalizedHome != "" && pathsEqual(normalized(resolved), normalizedHome, caseInsensitive) {
			return "", fmt.Errorf("target_dir resolve para o diretorio home do usuario ('%s').", resolved)
		}
	}

	if harnessBase := normalizedOrEmpty(binaryDirLocal()); harnessBase != "" && pathsEqual(normalized(resolved), harnessBase, caseInsensitive) {
		return "", fmt.Errorf("target_dir resolve para o diretorio de instalacao do harness ('%s').", resolved)
	}

	return resolved, nil
}

func binaryDirLocal() string {
	exe, err := os.Executable()
	if err != nil {
		return ""
	}
	return filepath.Dir(exe)
}

func normalized(path string) string {
	return strings.TrimRight(path, string(filepath.Separator))
}

func normalizedOrEmpty(path string) string {
	if strings.TrimSpace(path) == "" {
		return ""
	}
	abs, err := filepath.Abs(path)
	if err != nil {
		return ""
	}
	return normalized(abs)
}

func pathsEqual(a, b string, caseInsensitive bool) bool {
	if caseInsensitive {
		return strings.EqualFold(a, b)
	}
	return a == b
}

func appendProgress(targetDir string, featureId int, title, verifyCmd, verifyResult string) error {
	summary := oneLine(state(currentFeatureSummaryKey), "implementacao concluida")
	verify := oneLine(verifyResult, "PASS")
	command := verifyCmd
	if strings.TrimSpace(command) == "" {
		command = "comando de verificacao do projeto"
	}
	line := fmt.Sprintf("[%s UTC] Feature #%d - %s: %s. Verificar com: %s. Resultado: %s\n",
		time.Now().UTC().Format("2006-01-02 15:04"), featureId, oneLine(title, ""), summary, oneLine(command, ""), verify)

	f, err := os.OpenFile(filepath.Join(targetDir, "progress.txt"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()
	_, err = f.WriteString(line)
	return err
}

func commitMessage(featureId int, title string) string {
	suffix := oneLine(title, "")
	if len(suffix) > 72 {
		suffix = strings.TrimRight(truncateUtf8Bytes(suffix, 72), " \t")
	}
	return fmt.Sprintf("feat(development): complete feature #%d - %s", featureId, suffix)
}

// truncateUtf8Bytes cuts text at no more than maxBytes UTF-8 octets, backing off to a valid
// leading-byte boundary — never splits a multi-byte character (accent, emoji) in half.
// Duplicated from harnessengine on purpose (unexported there): this flow layer owns its own
// copy, mirroring the .NET/Rust ports where the same helper lives once per layer.
func truncateUtf8Bytes(text string, maxBytes int) string {
	if len(text) <= maxBytes {
		return text
	}

	cut := maxBytes
	for cut > 0 && (text[cut]&0xC0) == 0x80 {
		cut--
	}

	return text[:cut]
}

func oneLine(value, fallback string) string {
	normalized := strings.Join(strings.Fields(strings.ReplaceAll(strings.ReplaceAll(value, "\r", " "), "\n", " ")), " ")
	if strings.TrimSpace(normalized) == "" {
		return fallback
	}
	return strings.TrimSpace(normalized)
}
