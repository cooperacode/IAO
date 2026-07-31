package harnessengine

// HarnessState is the state persisted between invocations: step counter plus accumulated
// domain data.
type HarnessState struct {
	Step int               `json:"step"`
	Data map[string]string `json:"data"`

	// CostChars is the run's accumulated cost, input to the cost ceiling (see TaskRegistry).
	CostChars int `json:"costChars"`

	// Context is the driver context (e.g. {"driver":"claude code"}) captured in the `start`
	// envelope — survives across invocations so PromptFormatter can reinject it into every
	// output without each task having to pass it along.
	Context map[string]string `json:"context,omitempty"`

	// TerminalReason latches a hard stop across process boundaries.
	TerminalReason string `json:"terminalReason,omitempty"`
}

// NewHarnessState builds a state with the given step and data (data defaults to an empty,
// non-nil map).
func NewHarnessState(step int, data map[string]string) HarnessState {
	if data == nil {
		data = map[string]string{}
	}
	return HarnessState{Step: step, Data: data}
}
