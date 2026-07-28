//go:build windows

package main

import (
	"os/exec"
	"strconv"
	"syscall"
)

// setProcessGroup starts the child in its own process group (Windows has no pgid, but a
// process started with CREATE_NEW_PROCESS_GROUP roots a tree that taskkill /T can target).
func setProcessGroup(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: syscall.CREATE_NEW_PROCESS_GROUP}
}

// killProcessTree kills the process tree rooted at cmd.Process — taskkill /T walks child
// processes, which a plain Process.Kill() does not.
func killProcessTree(cmd *exec.Cmd) {
	_ = exec.Command("taskkill", "/T", "/F", "/PID", strconv.Itoa(cmd.Process.Pid)).Run()
}
