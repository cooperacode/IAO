//go:build !windows

package main

import (
	"os/exec"
	"syscall"
)

// setProcessGroup starts the child in its own process group so killProcessTree can kill
// the whole tree (the script may spawn children), not just the direct child.
func setProcessGroup(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
}

// killProcessTree kills the process group started by setProcessGroup.
func killProcessTree(cmd *exec.Cmd) {
	_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
}
