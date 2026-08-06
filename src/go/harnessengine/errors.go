package harnessengine

import "fmt"

// HarnessTimeoutError signals that a step's execution timeout (see
// HarnessConfig.TimeoutMs) was exceeded. Raised inside TaskRegistry and caught right there:
// it becomes a stderr diagnostic + "stop" on stdout — the same graceful-termination
// contract as the other guards (step and cost ceilings).
type HarnessTimeoutError struct {
	TimeoutMs int
}

func (e *HarnessTimeoutError) Error() string {
	return fmt.Sprintf("task execution exceeded the %dms timeout; stopping.", e.TimeoutMs)
}

// HarnessFaultError wraps a recovered panic from inside a task action — a bug in the
// harness/flow itself, not a driver protocol error the driver can fix by resending. Recovered
// in runProtected so it never crashes the process; surfaced as the "fault" trace outcome.
type HarnessFaultError struct {
	Reason string
}

func (e *HarnessFaultError) Error() string {
	return fmt.Sprintf("unhandled fault in task action: %s", e.Reason)
}
