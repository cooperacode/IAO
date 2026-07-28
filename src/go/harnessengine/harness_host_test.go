package harnessengine

import (
	"os"
	"testing"
)

func finalizeTask() map[string]Action {
	return map[string]Action{"finalize": func(*Envelope) string { return "stop" }}
}

func TestRun_OnCompletion_FreezesTrajectoryAndStateAtFlowPath(t *testing.T) {
	isolate(t)

	SetState("descricao", "x")

	Run([]string{`{"type":"command","value":"finalize"}`}, finalizeTask(), RunOptions{
		TraceSnapshotPath: LastRunTracePath,
		StateSnapshotPath: LastRunStatePath,
	})

	if !fileExists(LastRunTracePath) {
		t.Fatal("expected trace snapshot to exist")
	}
	if !fileExists(LastRunStatePath) {
		t.Fatal("expected state snapshot to exist")
	}
	if got := LoadStateFrom(LastRunStatePath).Data["descricao"]; got != "x" {
		t.Fatalf("unexpected snapshot data: %s", got)
	}
}

func TestRun_EvaluationDoesNotOverwriteRefinementEvidence(t *testing.T) {
	isolate(t)

	// 1) Refinement completes → last-run.* holds the refinement's evidence.
	SetState("descricao", "refino")
	Run([]string{`{"type":"command","value":"finalize"}`}, finalizeTask(), RunOptions{
		TraceSnapshotPath: LastRunTracePath,
		StateSnapshotPath: LastRunStatePath,
	})
	refinoTraceBytes, err := os.ReadFile(LastRunTracePath)
	if err != nil {
		t.Fatal(err)
	}
	refinoTrace := string(refinoTraceBytes)

	// 2) Evaluation completes using ITS OWN paths (last-evaluation.*).
	startTask := map[string]Action{"start": func(*Envelope) string { return "stop" }}
	Run([]string{`{"type":"text","value":"start"}`}, startTask, RunOptions{
		TraceSnapshotPath: LastEvaluationTracePath,
		StateSnapshotPath: LastEvaluationStatePath,
	})

	if !fileExists(LastEvaluationTracePath) {
		t.Fatal("expected evaluation trace snapshot to exist")
	}
	afterTraceBytes, err := os.ReadFile(LastRunTracePath)
	if err != nil {
		t.Fatal(err)
	}
	if string(afterTraceBytes) != refinoTrace {
		t.Fatal("evaluation must not touch the refinement's evidence")
	}
	if got := LoadStateFrom(LastRunStatePath).Data["descricao"]; got != "refino" {
		t.Fatalf("unexpected snapshot data: %s", got)
	}
}
