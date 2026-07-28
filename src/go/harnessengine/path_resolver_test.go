package harnessengine

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func TestResolvePath_AbsolutePath_ReturnsSamePath(t *testing.T) {
	isolate(t)
	dir := t.TempDir()
	absolute := filepath.Join(dir, "harness.json")

	if got := ResolvePath(absolute); got != absolute {
		t.Fatalf("unexpected result: %s", got)
	}
}

func TestResolvePath_ExistingRelativePath_ResolvesFromCwd(t *testing.T) {
	dir := isolate(t)

	os.WriteFile("harness.json", []byte("{}"), 0o644)
	resolved := ResolvePath("harness.json")

	expected, err := filepath.EvalSymlinks(filepath.Join(dir, "harness.json"))
	if err != nil {
		t.Fatal(err)
	}
	if resolved != expected {
		t.Fatalf("unexpected resolved path: %s vs %s", resolved, expected)
	}
}

func TestResolvePath_NonExistentRelativePath_FallsBackToBinaryDir(t *testing.T) {
	isolate(t)

	resolved := ResolvePath("um-arquivo-que-nao-existe.md")

	exeDir, err := filepath.EvalSymlinks(binaryDir())
	if err != nil {
		t.Fatal(err)
	}
	expected := filepath.Join(exeDir, "um-arquivo-que-nao-existe.md")
	if resolved != expected {
		t.Fatalf("unexpected resolved path: %s vs %s", resolved, expected)
	}
}

func TestResolvePath_SymlinkEscapingCwd_DoesNotFollowTheLink(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlinks require elevated privileges on windows")
	}
	isolate(t)

	outside := t.TempDir()
	secret := filepath.Join(outside, "secreto.txt")
	if err := os.WriteFile(secret, []byte("segredo"), 0o644); err != nil {
		t.Fatal(err)
	}
	secretCanonical, err := filepath.EvalSymlinks(secret)
	if err != nil {
		t.Fatal(err)
	}

	if err := os.Symlink(secret, "link.txt"); err != nil {
		t.Fatal(err)
	}
	resolved := ResolvePath("link.txt")

	// The link exists and points outside the CWD — must not be returned as the real path
	// it resolves to (that would leak the escape); falls back to the binary dir.
	if resolved == secretCanonical {
		t.Fatalf("expected the symlink escape to be rejected, got %s", resolved)
	}
}
