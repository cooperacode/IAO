package harnessengine

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// LoadArtifactTemplate reads .harness/skills/<name>/ARTIFACT.md — an output template with
// `{{key}}` placeholders substituted with StateStore values. The artifact's markdown shape
// lives alongside the skill that produces it — outside Go code, editable without
// recompiling. Returns "" (ok=false) if the skill defines no template — the caller decides
// the fallback.
func LoadArtifactTemplate(skillName string) (string, bool) {
	path := ResolvePath(filepath.Join(".harness", "skills", skillName, "ARTIFACT.md"))
	if !fileExists(path) {
		return "", false
	}
	data, err := os.ReadFile(path)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[ArtifactTemplate] failed to read template from %s: %s\n", skillName, err)
		return "", false
	}
	return string(data), true
}

// RenderArtifactTemplate replaces each `{{key}}` with its matching value. Placeholders with
// no value remain in the text — a visible sign of missing data, not a silent error.
func RenderArtifactTemplate(template string, values map[string]string) string {
	result := template
	for key, value := range values {
		result = strings.ReplaceAll(result, "{{"+key+"}}", value)
	}
	return result
}
