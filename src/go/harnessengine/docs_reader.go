package harnessengine

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// Reads a set of documents (*.md and *.txt) from a folder to inject into the prompt. It is
// the alternative input to the interactive one: the flow reads material that already
// exists (specs, notes, transcripts) and the model synthesizes a brief from it.
//
// Analogous to how PromptFormatter injects skills — the reading is deterministic (done in
// code), only the synthesis is left to the model.

var docsExtensions = map[string]bool{".md": true, ".txt": true}

// HasDocs reports whether the folder exists and has at least one *.md/*.txt file.
func HasDocs(folder string) bool {
	dir := ResolvePath(folder)
	if !dirExists(dir) {
		return false
	}
	return len(docFiles(dir)) > 0
}

// ReadDocs concatenates the documents in alphabetical order, each under a
// `## <file-name>` heading, and also returns the list of names (to cite sources).
func ReadDocs(folder string) (string, []string) {
	dir := ResolvePath(folder)
	if !dirExists(dir) {
		return "", []string{}
	}

	files := docFiles(dir)
	names := make([]string, 0, len(files))
	var sb strings.Builder
	maxChars := CurrentConfig().DocsMaxChars

	for _, path := range files {
		name := filepath.Base(path)
		data, err := os.ReadFile(path)
		if err != nil {
			fmt.Fprintf(os.Stderr, "[DocsReader] failed to read %s: %s\n", name, err)
			continue
		}

		names = append(names, name)
		sb.WriteString("## ")
		sb.WriteString(name)
		sb.WriteString("\n\n")
		sb.Write(data)
		sb.WriteString("\n\n")

		if sb.Len() > maxChars {
			fmt.Fprintf(os.Stderr, "[DocsReader] content exceeded %d bytes (UTF-8); truncating at %s.\n", maxChars, name)
			truncated := truncateUtf8Bytes(sb.String(), maxChars)
			sb.Reset()
			sb.WriteString(truncated)
			break
		}
	}

	return strings.TrimRight(sb.String(), " \t\r\n"), names
}

// truncateUtf8Bytes cuts text at no more than maxBytes UTF-8 octets, backing off to a valid
// leading-byte boundary — never splits a multi-byte character (accent, emoji) in half,
// which would produce invalid bytes/replacement characters.
func truncateUtf8Bytes(text string, maxBytes int) string {
	if len(text) <= maxBytes {
		return text
	}

	cut := maxBytes
	// A UTF-8 continuation byte has its two high bits set to "10" (0x80..0xBF); backing off
	// to a byte that is NOT a continuation guarantees [0, cut) is a complete sequence.
	for cut > 0 && (text[cut]&0xC0) == 0x80 {
		cut--
	}

	return text[:cut]
}

func docFiles(dir string) []string {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}

	var files []string
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		ext := strings.ToLower(filepath.Ext(entry.Name()))
		if docsExtensions[ext] {
			files = append(files, filepath.Join(dir, entry.Name()))
		}
	}

	sort.Slice(files, func(i, j int) bool {
		return strings.ToLower(filepath.Base(files[i])) < strings.ToLower(filepath.Base(files[j]))
	})
	return files
}
