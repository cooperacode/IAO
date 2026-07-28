package harnessengine

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"unicode/utf8"
)

func TestDocs_HasDocs_MissingFolder_False(t *testing.T) {
	dir := isolate(t)
	missing := filepath.Join(dir, "nao-existe")

	if HasDocs(missing) {
		t.Fatal("expected false")
	}
}

func TestDocs_HasDocs_EmptyFolder_False(t *testing.T) {
	dir := isolate(t)

	if HasDocs(dir) {
		t.Fatal("expected false")
	}
}

func TestDocs_HasDocs_IgnoresUnsupportedExtensions(t *testing.T) {
	dir := isolate(t)
	os.WriteFile(filepath.Join(dir, "imagem.png"), []byte("x"), 0o644)
	os.WriteFile(filepath.Join(dir, "dados.json"), []byte("{}"), 0o644)

	if HasDocs(dir) {
		t.Fatal("expected false")
	}
}

func TestDocs_HasDocs_WithMarkdown_True(t *testing.T) {
	dir := isolate(t)
	os.WriteFile(filepath.Join(dir, "spec.md"), []byte("conteúdo"), 0o644)

	if !HasDocs(dir) {
		t.Fatal("expected true")
	}
}

func TestDocs_Read_ConcatenatesMdAndTxtAlphabetically(t *testing.T) {
	dir := isolate(t)
	os.WriteFile(filepath.Join(dir, "b-notas.txt"), []byte("notas"), 0o644)
	os.WriteFile(filepath.Join(dir, "a-spec.md"), []byte("spec"), 0o644)

	content, files := ReadDocs(dir)

	if len(files) != 2 || files[0] != "a-spec.md" || files[1] != "b-notas.txt" {
		t.Fatalf("unexpected files: %+v", files)
	}
	if !strings.Contains(content, "## a-spec.md") || !strings.Contains(content, "## b-notas.txt") {
		t.Fatalf("unexpected content: %s", content)
	}
	if strings.Index(content, "a-spec.md") > strings.Index(content, "b-notas.txt") {
		t.Fatalf("unexpected order: %s", content)
	}
}

func TestDocs_Read_MissingFolder_EmptyNoSources(t *testing.T) {
	dir := isolate(t)
	missing := filepath.Join(dir, "nao-existe")

	content, files := ReadDocs(missing)

	if content != "" || len(files) != 0 {
		t.Fatalf("unexpected result: %q %+v", content, files)
	}
}

func TestTruncateUtf8Bytes_NeverSplitsMultiByteCharacter(t *testing.T) {
	text := "café ☕" // "é" (2 bytes) and "☕" (3 bytes) are multi-byte.
	for max := 0; max <= len(text); max++ {
		truncated := truncateUtf8Bytes(text, max)
		if len(truncated) > max {
			t.Fatalf("truncated exceeded max: %d > %d", len(truncated), max)
		}
		if !utf8.ValidString(truncated) {
			t.Fatalf("invalid utf-8 at max=%d: %q", max, truncated)
		}
	}
}

func TestDocs_Read_TruncatesAtValidUtf8Boundary(t *testing.T) {
	dir := isolate(t)
	os.WriteFile(filepath.Join(dir, "a.md"), []byte("café ☕"), 0o644)

	content, _ := ReadDocs(dir)

	if !strings.Contains(content, "café ☕") {
		t.Fatalf("unexpected content: %s", content)
	}
}
