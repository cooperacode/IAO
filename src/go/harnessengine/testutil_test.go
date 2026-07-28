package harnessengine

import (
	"os"
	"testing"
)

// isolate creates a fresh temp dir, chdirs into it for the duration of the test, and
// restores the previous cwd on cleanup. Go runs a package's tests sequentially by default
// (no goroutine interleaving across tests), so mutating the process-wide cwd here is safe
// without the cross-thread lock the Rust port needs.
func isolate(t *testing.T) string {
	t.Helper()

	dir := t.TempDir()
	previous, err := os.Getwd()
	if err != nil {
		t.Fatalf("getwd: %v", err)
	}
	if err := os.Chdir(dir); err != nil {
		t.Fatalf("chdir: %v", err)
	}

	t.Cleanup(func() {
		_ = os.Chdir(previous)
		ReloadConfig()
	})

	// Fresh config cache scoped to this test's cwd — CurrentConfig() caches per process,
	// which would otherwise leak a previous test's harness.json into this one.
	ReloadConfig()

	return dir
}
