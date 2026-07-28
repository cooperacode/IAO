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
	return fmt.Sprintf("timeout de %dms excedido na execução da task; encerrando.", e.TimeoutMs)
}
