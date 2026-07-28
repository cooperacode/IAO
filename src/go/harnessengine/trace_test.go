package harnessengine

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"strings"
	"testing"
)

func TestTrace_AppendAndLoad_RoundTripInWriteOrder(t *testing.T) {
	isolate(t)

	AppendTrace(1, "start", TraceOutcome.Instruction, 42, "")
	AppendTrace(2, "classify", TraceOutcome.Instruction, 99, "")

	entries := LoadTrace()
	if len(entries) != 2 {
		t.Fatalf("unexpected count: %d", len(entries))
	}
	if entries[0].Step != 1 || entries[0].Command != "start" || entries[0].InstructionChars != 42 {
		t.Fatalf("unexpected first entry: %+v", entries[0])
	}
	if entries[1].Step != 2 || entries[1].Command != "classify" || entries[1].InstructionChars != 99 {
		t.Fatalf("unexpected second entry: %+v", entries[1])
	}
	if entries[0].Timestamp == "" {
		t.Fatal("expected non-empty timestamp")
	}
}

func TestTrace_FirstEntry_HasGenesisPrevHash(t *testing.T) {
	isolate(t)

	AppendTrace(1, "start", TraceOutcome.Instruction, 1, "")

	if got := LoadTrace()[0].PrevHash; got != strings.Repeat("0", 64) {
		t.Fatalf("unexpected genesis hash: %s", got)
	}
}

func TestTrace_EachEntry_ChainsPrevHashWithSha256OfPreviousLine(t *testing.T) {
	isolate(t)

	AppendTrace(1, "start", TraceOutcome.Instruction, 42, "")
	AppendTrace(2, "classify", TraceOutcome.Instruction, 99, "")
	AppendTrace(3, "finalize", TraceOutcome.Stop, 5, "")

	raw, err := os.ReadFile(traceFilePath)
	if err != nil {
		t.Fatal(err)
	}
	rawLines := strings.Split(strings.TrimRight(string(raw), "\n"), "\n")
	entries := LoadTrace()

	sum := sha256.Sum256([]byte(rawLines[0]))
	if entries[1].PrevHash != hex.EncodeToString(sum[:]) {
		t.Fatalf("unexpected prevHash: %s", entries[1].PrevHash)
	}

	sum = sha256.Sum256([]byte(rawLines[1]))
	if entries[2].PrevHash != hex.EncodeToString(sum[:]) {
		t.Fatalf("unexpected prevHash: %s", entries[2].PrevHash)
	}
}

func TestTrace_ResetThenAppend_RestartsChainWithGenesis(t *testing.T) {
	isolate(t)

	AppendTrace(1, "start", TraceOutcome.Instruction, 1, "")
	AppendTrace(2, "classify", TraceOutcome.Instruction, 1, "")

	ResetTrace()
	AppendTrace(1, "start", TraceOutcome.Instruction, 1, "")

	entries := LoadTrace()
	if len(entries) != 1 {
		t.Fatalf("unexpected count: %d", len(entries))
	}
	if entries[0].PrevHash != strings.Repeat("0", 64) {
		t.Fatalf("unexpected prevHash: %s", entries[0].PrevHash)
	}
}

func TestTrace_DeserializeLegacyWithoutPrevHash_DefaultsToEmpty(t *testing.T) {
	isolate(t)

	if err := ensureDir(traceDir); err != nil {
		t.Fatal(err)
	}
	legacy := `{"step":1,"command":"start","outcome":"instruction","instructionChars":1,"timestamp":"2026-01-01T00:00:00Z"}`
	if err := os.WriteFile(traceFilePath, []byte(legacy+"\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	entries := LoadTrace()
	if len(entries) != 1 || entries[0].PrevHash != "" {
		t.Fatalf("unexpected entries: %+v", entries)
	}
}

func TestTrace_Load_MissingFile_ReturnsEmpty(t *testing.T) {
	isolate(t)

	if len(LoadTrace()) != 0 {
		t.Fatal("expected empty")
	}
}

func TestTrace_Reset_TruncatesPreviousTrace(t *testing.T) {
	isolate(t)

	AppendTrace(1, "start", TraceOutcome.Instruction, 1, "")
	if len(LoadTrace()) != 1 {
		t.Fatal("expected one entry")
	}

	ResetTrace()

	if len(LoadTrace()) != 0 {
		t.Fatal("expected empty after reset")
	}
}

func TestTrace_Snapshot_CopiesLiveTraceToDestination(t *testing.T) {
	isolate(t)

	AppendTrace(1, "start", TraceOutcome.Instruction, 1, "")

	SnapshotTrace(LastRunTracePath)

	if len(LoadTraceFrom(LastRunTracePath)) != 1 {
		t.Fatal("expected snapshot to contain the entry")
	}
}

func TestTrace_AppendWithLabel_PersistsLabel(t *testing.T) {
	isolate(t)

	AppendTrace(1, "pick", TraceOutcome.Instruction, 1, "feature:3")

	if got := LoadTrace()[0].Label; got != "feature:3" {
		t.Fatalf("unexpected label: %s", got)
	}
}
