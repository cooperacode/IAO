package harnessengine

import (
	"fmt"
	"os"
	"time"
)

// writeAtomic writes content to path atomically: writes to a unique temp file in the SAME
// directory as the destination, then swaps it in via os.Rename — atomic on the same
// filesystem. A crash/kill mid-write can never leave the final file truncated or partially
// overwritten; a concurrent reader always sees either the previous complete version or the
// new complete one, never an intermediate state. Does not apply to the append-only writes
// of log/trace files — those are already atomic at the event level (one line, one call)
// and don't need a file swap.
func writeAtomic(path, content string) error {
	tmp := tempPathFor(path)
	if err := os.WriteFile(tmp, []byte(content), 0o644); err != nil {
		cleanupBestEffort(tmp)
		return err
	}
	if err := os.Rename(tmp, path); err != nil {
		cleanupBestEffort(tmp)
		return err
	}
	return nil
}

// copyAtomic offers the same atomicity guarantee as writeAtomic, but copies from an
// existing source file (e.g. snapshotting a live store into its frozen counterpart).
func copyAtomic(sourcePath, destinationPath string) error {
	content, err := os.ReadFile(sourcePath)
	if err != nil {
		return err
	}
	return writeAtomic(destinationPath, string(content))
}

// tempPathFor builds a unique name in the SAME directory as destination — os.CreateTemp
// with "" would pick the OS temp dir, breaking the same-filesystem rename guarantee.
func tempPathFor(destination string) string {
	return fmt.Sprintf("%s.tmp-%d-%d", destination, os.Getpid(), time.Now().UnixNano())
}

func cleanupBestEffort(tmp string) {
	_ = os.Remove(tmp)
}

// ensureDir creates dir (and parents) if needed, ignoring the case where it already exists.
func ensureDir(dir string) error {
	return os.MkdirAll(dir, 0o755)
}

func fileExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

func dirExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}
