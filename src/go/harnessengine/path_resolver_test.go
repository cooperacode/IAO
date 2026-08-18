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

	resolved := ResolvePath("a-file-that-does-not-exist.md")

	exeDir, err := filepath.EvalSymlinks(binaryDir())
	if err != nil {
		t.Fatal(err)
	}
	expected := filepath.Join(exeDir, "a-file-that-does-not-exist.md")
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

// Regression test for a real production bug: on macOS /tmp is itself a symlink to
// /private/tmp, so a harness process invoked with a cwd under /tmp used to resolve a real,
// present file's path via filepath.EvalSymlinks (giving the "/private/tmp/..." form) and
// then compare it against the unresolved "/tmp/..." cwd inside isContained — the prefix
// check never matched, so ResolvePath silently fell through to "not found" and readSkills
// embedded nothing into the prompt, with no error anywhere. t.TempDir() doesn't reproduce
// this on its own on this machine (it isn't reached through a symlink here), so this test
// builds an explicit symlinked cwd to pin the exact failure mode down. Verified directly
// against a standalone binary before this fix: ResolvePath("<skill>.md") from a cwd under
// /tmp resolved to "/private/tmp/<skill>.md" (missing the real subdirectory entirely) and
// reported the file as not existing, even though it was right there.
func TestResolvePath_CwdItselfBehindASymlink_StillResolvesTheRealFile(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlinks require elevated privileges on windows")
	}

	real := t.TempDir()
	link := filepath.Join(t.TempDir(), "linked-cwd")
	if err := os.Symlink(real, link); err != nil {
		t.Fatal(err)
	}

	previous, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Chdir(link); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chdir(previous) })

	if err := os.WriteFile("harness.json", []byte("{}"), 0o644); err != nil {
		t.Fatal(err)
	}

	resolved := ResolvePath("harness.json")
	expected, err := filepath.EvalSymlinks(filepath.Join(real, "harness.json"))
	if err != nil {
		t.Fatal(err)
	}
	if resolved != expected {
		t.Fatalf("a cwd reached through a symlink should still resolve an existing file: got %s, want %s", resolved, expected)
	}
}
