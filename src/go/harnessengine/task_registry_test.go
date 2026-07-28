package harnessengine

import (
	"os"
	"strings"
	"testing"
	"time"
)

func testTasks() map[string]Action {
	return map[string]Action{
		"start": func(*Envelope) string { return "PROMPT_START" },
		"classify": func(e *Envelope) string {
			arg := ""
			if e != nil && len(e.Args) > 0 {
				arg = e.Args[0]
			}
			return "PROMPT_CLASSIFY:" + arg
		},
		"finalize": func(*Envelope) string { return "stop" },
	}
}

func TestDispatch_RegisteredCommand_ExecutesAction(t *testing.T) {
	isolate(t)

	result := Dispatch([]string{`{"type":"text","value":"start"}`}, testTasks(), nil, nil, nil)

	if result != "PROMPT_START" {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestDispatch_PassesArgsToAction(t *testing.T) {
	isolate(t)

	result := Dispatch([]string{`{"type":"tool","value":"classify","args":["Login"]}`}, testTasks(), nil, nil, nil)

	if result != "PROMPT_CLASSIFY:Login" {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestDispatch_Finalize_ReturnsStop(t *testing.T) {
	isolate(t)

	result := Dispatch([]string{`{"type":"command","value":"finalize"}`}, testTasks(), nil, nil, nil)

	if result != "stop" {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestDispatch_UnknownCommand_ReturnsErrorNotStop(t *testing.T) {
	isolate(t)

	result := Dispatch([]string{`{"type":"text","value":"tipo"}`}, testTasks(), nil, nil, nil)

	if !strings.HasPrefix(result, "ERRO") || result == "stop" || !strings.Contains(result, "'tipo'") {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestDispatch_MalformedJSON_ReturnsErrorNotStop(t *testing.T) {
	isolate(t)

	result := Dispatch([]string{`{"type":"text","value":`}, testTasks(), nil, nil, nil)

	if !strings.HasPrefix(result, "ERRO") || result == "stop" {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestDispatch_NoArgument_ReturnsErrorNotStop(t *testing.T) {
	isolate(t)

	result := Dispatch(nil, testTasks(), nil, nil, nil)

	if !strings.HasPrefix(result, "ERRO") || result == "stop" {
		t.Fatalf("unexpected result: %s", result)
	}
}

func TestDispatch_ErrorMessage_ListsValidCommands(t *testing.T) {
	isolate(t)

	result := Dispatch([]string{`{"type":"text","value":"inexistente"}`}, testTasks(), nil, nil, nil)

	for _, cmd := range []string{"start", "classify", "finalize"} {
		if !strings.Contains(result, cmd) {
			t.Fatalf("expected %q in result: %s", cmd, result)
		}
	}
}

func TestDispatch_Start_ResetsStepCounter(t *testing.T) {
	isolate(t)

	for i := 0; i < 5; i++ {
		Dispatch([]string{`{"type":"tool","value":"classify","args":["x"]}`}, testTasks(), nil, nil, nil)
	}
	if LoadState().Step != 5 {
		t.Fatalf("unexpected step: %d", LoadState().Step)
	}

	Dispatch([]string{`{"type":"text","value":"start"}`}, testTasks(), nil, nil, nil)

	// start resets, then counts itself as step 1.
	if LoadState().Step != 1 {
		t.Fatalf("unexpected step after start: %d", LoadState().Step)
	}
}

func TestDispatch_StartWithShouldResetOnStartFalse_DoesNotTruncateStateOrTrace(t *testing.T) {
	isolate(t)

	for i := 0; i < 3; i++ {
		Dispatch([]string{`{"type":"tool","value":"classify","args":["x"]}`}, testTasks(), nil, nil, nil)
	}
	AppendTrace(99, "handoff", TraceOutcome.Instruction, 5, "")

	neverReset := func() bool { return false }
	Dispatch([]string{`{"type":"text","value":"start"}`}, testTasks(), nil, nil, neverReset)

	if LoadState().Step != 4 { // 3 previous + the "start" itself, without reset
		t.Fatalf("unexpected step: %d", LoadState().Step)
	}
	found := false
	for _, e := range LoadTrace() {
		if e.Step == 99 && e.Command == "handoff" {
			found = true
		}
	}
	if !found {
		t.Fatal("expected the pre-reset trace entry to survive")
	}
}

func TestDispatch_StartWithoutPredicate_KeepsDefaultAlwaysResetBehavior(t *testing.T) {
	isolate(t)

	for i := 0; i < 3; i++ {
		Dispatch([]string{`{"type":"tool","value":"classify","args":["x"]}`}, testTasks(), nil, nil, nil)
	}

	Dispatch([]string{`{"type":"text","value":"start"}`}, testTasks(), nil, nil, nil)

	if LoadState().Step != 1 { // backward compatible: no predicate means always reset
		t.Fatalf("unexpected step: %d", LoadState().Step)
	}
}

func TestDispatch_StartWithContext_PersistsInStateStore(t *testing.T) {
	isolate(t)

	Dispatch([]string{`{"type":"text","value":"start","context":{"driver":"claude code"}}`}, testTasks(), nil, nil, nil)

	if got := GetContext(); got["driver"] != "claude code" {
		t.Fatalf("unexpected context: %+v", got)
	}
}

func TestDispatch_ExceedingCeiling_ForcesStop(t *testing.T) {
	isolate(t)

	maxSteps := 3
	for i := 0; i < maxSteps; i++ {
		result := Dispatch([]string{`{"type":"tool","value":"classify","args":["x"]}`}, testTasks(), nil, &maxSteps, nil)
		if result == "stop" {
			t.Fatalf("unexpected early stop at step %d", i)
		}
	}

	result := Dispatch([]string{`{"type":"tool","value":"classify","args":["x"]}`}, testTasks(), nil, &maxSteps, nil)
	if result != "stop" {
		t.Fatalf("expected stop, got %s", result)
	}
}

func TestDispatch_SlowTask_TimesOutAndReturnsStop(t *testing.T) {
	isolate(t)

	os.Setenv("HARNESS_TIMEOUT_MS", "50")
	ReloadConfig()
	defer func() {
		os.Unsetenv("HARNESS_TIMEOUT_MS")
		ReloadConfig()
	}()

	slow := map[string]Action{
		"slow": func(*Envelope) string {
			time.Sleep(500 * time.Millisecond)
			return "nunca chega"
		},
	}

	result := Dispatch([]string{`{"type":"command","value":"slow"}`}, slow, nil, nil, nil)

	if result != "stop" {
		t.Fatalf("expected stop, got %s", result)
	}
}
