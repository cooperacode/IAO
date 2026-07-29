package harnessengine

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// Persists each flow artifact in its own file (.harness/<name>.md) and keeps a manifest
// (.harness/artifacts.json) with the write order. The manifest is the contract between
// producer and consumer: the evaluation reads artifacts through it, without depending on a
// combined report.
//
// Only the PRODUCER flow resets the manifest (on its `start`) — the consumer (evaluation)
// never touches it, for the same reason as the Trace/StateStore snapshots: the evaluator's
// start must not erase the evidence it's about to read.
const (
	artifactDir          = ".harness"
	ArtifactManifestPath = ".harness/artifacts.json"
)

type artifactManifest struct {
	Files []string `json:"files"`
}

// ResetArtifacts deletes the previous run's artifacts and the manifest — called by the
// producer flow on start.
func ResetArtifacts() {
	for _, file := range ArtifactFiles() {
		if fileExists(file) {
			if err := os.Remove(file); err != nil {
				fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to clear: %s\n", err)
			}
		}
	}
	if fileExists(ArtifactManifestPath) {
		if err := os.Remove(ArtifactManifestPath); err != nil {
			fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to clear: %s\n", err)
		}
	}
}

// WriteArtifact writes .harness/<name>.md and registers the path in the manifest (once, in
// arrival order).
func WriteArtifact(name, content string) string {
	path := filepath.Join(artifactDir, name+".md")

	if err := ensureDir(artifactDir); err != nil {
		fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to write %s: %s\n", name, err)
		return path
	}
	if err := writeAtomic(path, content); err != nil {
		fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to write %s: %s\n", name, err)
		return path
	}

	files := ArtifactFiles()
	found := false
	for _, f := range files {
		if f == path {
			found = true
			break
		}
	}
	if !found {
		files = append(files, path)
		saveArtifactManifest(files)
	}

	return path
}

// ArtifactFiles returns the paths registered in the manifest, in the order they were written.
func ArtifactFiles() []string {
	if fileExists(ArtifactManifestPath) {
		data, err := os.ReadFile(ArtifactManifestPath)
		if err == nil {
			var manifest artifactManifest
			if err := json.Unmarshal(data, &manifest); err == nil {
				if manifest.Files == nil {
					return []string{}
				}
				return manifest.Files
			}
			fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to load manifest: %s\n", err)
		} else {
			fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to load manifest: %s\n", err)
		}
	}
	return []string{}
}

// HasArtifacts reports whether any artifacts are recorded and still present on disk.
func HasArtifacts() bool {
	for _, f := range ArtifactFiles() {
		if fileExists(f) {
			return true
		}
	}
	return false
}

// ReadArtifact reads a single artifact by name (e.g. for reinjection into prompts). "" if
// absent/unreadable.
func ReadArtifact(name string) string {
	path := filepath.Join(artifactDir, name+".md")
	if !fileExists(path) {
		return ""
	}
	data, err := os.ReadFile(path)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to read %s: %s\n", name, err)
		return ""
	}
	return string(data)
}

// ReadAllArtifacts concatenates the artifacts in manifest order — the LLM judge's input.
func ReadAllArtifacts() string {
	var sb strings.Builder
	for _, file := range ArtifactFiles() {
		if !fileExists(file) {
			continue
		}
		data, err := os.ReadFile(file)
		if err != nil {
			fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to read %s: %s\n", file, err)
			continue
		}
		sb.WriteString(strings.TrimRight(string(data), " \t\r\n"))
		sb.WriteString("\n\n")
	}
	return strings.TrimRight(sb.String(), " \t\r\n")
}

func saveArtifactManifest(files []string) {
	if err := ensureDir(artifactDir); err != nil {
		fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to load manifest: %s\n", err)
		return
	}
	data, err := json.Marshal(artifactManifest{Files: files})
	if err != nil {
		fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to load manifest: %s\n", err)
		return
	}
	if err := writeAtomic(ArtifactManifestPath, string(data)); err != nil {
		fmt.Fprintf(os.Stderr, "[ArtifactStore] failed to load manifest: %s\n", err)
	}
}
