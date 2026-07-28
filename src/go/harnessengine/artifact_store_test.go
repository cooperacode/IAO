package harnessengine

import (
	"os"
	"strings"
	"testing"
)

func TestArtifacts_Write_WritesFileAndRegistersManifest(t *testing.T) {
	isolate(t)

	path := WriteArtifact("historias", "# Histórias\n\n1. a")

	if !fileExists(path) {
		t.Fatal("expected file to exist")
	}
	files := ArtifactFiles()
	if len(files) != 1 || files[0] != path {
		t.Fatalf("unexpected manifest: %+v", files)
	}
}

func TestArtifacts_WriteSameNameTwice_OverwritesWithoutDuplicating(t *testing.T) {
	isolate(t)

	WriteArtifact("historias", "v1")
	path := WriteArtifact("historias", "v2")

	if len(ArtifactFiles()) != 1 {
		t.Fatalf("expected single manifest entry, got %+v", ArtifactFiles())
	}
	data, err := os.ReadFile(path)
	if err != nil || string(data) != "v2" {
		t.Fatalf("unexpected content: %s (err=%v)", data, err)
	}
}

func TestArtifacts_ReadAll_ConcatenatesInWriteOrder(t *testing.T) {
	isolate(t)

	WriteArtifact("item", "# Item")
	WriteArtifact("historias", "# Histórias")

	all := ReadAllArtifacts()
	if strings.Index(all, "# Item") > strings.Index(all, "# Histórias") {
		t.Fatalf("unexpected order: %s", all)
	}
}

func TestArtifacts_Read_ReturnsWrittenContent(t *testing.T) {
	isolate(t)

	WriteArtifact("brief", "# Brief\n\nConstrua X.")

	if got := ReadArtifact("brief"); got != "# Brief\n\nConstrua X." {
		t.Fatalf("unexpected content: %s", got)
	}
}

func TestArtifacts_Read_MissingName_ReturnsEmpty(t *testing.T) {
	isolate(t)

	if got := ReadArtifact("nunca-gravado"); got != "" {
		t.Fatalf("expected empty, got %s", got)
	}
}

func TestArtifacts_Reset_DeletesArtifactsAndManifest(t *testing.T) {
	isolate(t)

	path := WriteArtifact("historias", "x")
	ResetArtifacts()

	if fileExists(path) {
		t.Fatal("expected artifact deleted")
	}
	if HasArtifacts() {
		t.Fatal("expected no artifacts")
	}
	if len(ArtifactFiles()) != 0 {
		t.Fatal("expected empty manifest")
	}
}
