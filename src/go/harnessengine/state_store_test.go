package harnessengine

import "testing"

func TestState_SetAndGet_PersistAcrossCalls(t *testing.T) {
	isolate(t)

	SetState("description", "Login with Google")

	if got := GetState("description"); got == nil || *got != "Login with Google" {
		t.Fatalf("unexpected value: %v", got)
	}
}

func TestState_Get_MissingKey_ReturnsNil(t *testing.T) {
	isolate(t)

	if got := GetState("does-not-exist"); got != nil {
		t.Fatalf("expected nil, got %v", *got)
	}
}

func TestState_Set_OverwritesExistingKey(t *testing.T) {
	isolate(t)

	SetState("type", "Bug")
	SetState("type", "Epic")

	if got := GetState("type"); got == nil || *got != "Epic" {
		t.Fatalf("unexpected value: %v", got)
	}
}

func TestState_Increment_AdvancesCounter(t *testing.T) {
	isolate(t)

	if IncrementStep() != 1 || IncrementStep() != 2 || IncrementStep() != 3 {
		t.Fatal("unexpected step sequence")
	}
	if LoadState().Step != 3 {
		t.Fatalf("unexpected step: %d", LoadState().Step)
	}
}

func TestState_Increment_PreservesAccumulatedData(t *testing.T) {
	isolate(t)

	SetState("description", "x")
	IncrementStep()

	if got := GetState("description"); got == nil || *got != "x" {
		t.Fatalf("unexpected value: %v", got)
	}
}

func TestState_Reset_ClearsCounterAndData(t *testing.T) {
	isolate(t)

	SetState("description", "x")
	IncrementStep()
	ResetState()

	if LoadState().Step != 0 {
		t.Fatalf("unexpected step: %d", LoadState().Step)
	}
	if GetState("description") != nil {
		t.Fatal("expected data cleared")
	}
}

func TestState_SetContextAndGetContext_PersistAcrossCalls(t *testing.T) {
	isolate(t)

	SetContext(map[string]string{"driver": "claude code"})

	if got := GetContext(); got["driver"] != "claude code" {
		t.Fatalf("unexpected context: %v", got)
	}
}

func TestState_GetContext_WithoutContextSet_ReturnsNil(t *testing.T) {
	isolate(t)

	if GetContext() != nil {
		t.Fatal("expected nil context")
	}
}

func TestState_Reset_ClearsContext(t *testing.T) {
	isolate(t)

	SetContext(map[string]string{"driver": "claude code"})
	ResetState()

	if GetContext() != nil {
		t.Fatal("expected context cleared")
	}
}

func TestState_AddCost_AccumulatesAcrossCalls(t *testing.T) {
	isolate(t)

	if AddCost(10) != 10 {
		t.Fatal("unexpected first cost")
	}
	if AddCost(5) != 15 {
		t.Fatal("unexpected accumulated cost")
	}
}
