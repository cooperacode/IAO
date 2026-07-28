package harnessengine

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
)

// ResolvePath resolves a relative path against the working directory (repo root, from
// where the driver invokes the harness), falling back to the binary's directory. Shared by
// anything that injects files into the prompt (skills, docs).
func ResolvePath(path string) string {
	trimmed := strings.TrimSpace(path)
	if filepath.IsAbs(trimmed) {
		return trimmed
	}

	if cwd, err := os.Getwd(); err == nil {
		fromCwd := filepath.Join(cwd, trimmed)
		if pathExists(fromCwd) && isContained(fromCwd, cwd) {
			if canonical, err := filepath.EvalSymlinks(fromCwd); err == nil {
				return canonical
			}
			return fromCwd
		}
	}

	baseDir := binaryDir()
	fromBase := filepath.Join(baseDir, trimmed)
	if pathExists(fromBase) && isContained(fromBase, baseDir) {
		if canonical, err := filepath.EvalSymlinks(fromBase); err == nil {
			return canonical
		}
		return fromBase
	}

	// Neither the CWD nor the binary dir served — absent, or a symlink diverting the
	// target outside both authorized bases. Returns the original join path WITHOUT
	// following the link (not the resolved target, which would be outside the base); the
	// caller's subsequent existence check naturally fails when the target isn't reachable.
	return fromBase
}

func binaryDir() string {
	exe, err := os.Executable()
	if err != nil {
		if cwd, err := os.Getwd(); err == nil {
			return cwd
		}
		return "."
	}
	dir := filepath.Dir(exe)
	if canonical, err := filepath.EvalSymlinks(dir); err == nil {
		return canonical
	}
	return dir
}

func pathExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

// isContained checks symlink containment (RFC §6.3): resolves candidate's final target (if
// it is a symlink) and confirms it is actually inside baseDir, comparing canonical paths by
// real directory prefix — not lexical string prefix (which would let "/base-evil" pass as
// contained in "/base").
func isContained(candidate, baseDir string) bool {
	normalizedBase, err := filepath.Abs(baseDir)
	if err != nil {
		normalizedBase = baseDir
	}
	normalizedBase = strings.TrimRight(normalizedBase, string(filepath.Separator))

	target := resolveFinalTarget(candidate)
	if abs, err := filepath.Abs(target); err == nil {
		target = abs
	}
	target = strings.TrimRight(target, string(filepath.Separator))

	if runtime.GOOS == "windows" {
		normalizedBase = strings.ToLower(normalizedBase)
		target = strings.ToLower(target)
	}

	if target == normalizedBase {
		return true
	}

	return strings.HasPrefix(target, normalizedBase+string(filepath.Separator))
}

// resolveFinalTarget follows the link (if any) to its final target; returns the path
// itself if it is not a link or resolution fails (e.g. broken link).
func resolveFinalTarget(path string) string {
	resolved, err := filepath.EvalSymlinks(path)
	if err != nil {
		return path
	}
	return resolved
}
