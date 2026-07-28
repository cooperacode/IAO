package harnessengine

import (
	"os"
	"testing"
)

func TestInbox_Read_MissingFile_ReturnsEmpty(t *testing.T) {
	isolate(t)

	if got := ReadInbox(); got != "" {
		t.Fatalf("expected empty, got %s", got)
	}
}

func TestInbox_Read_WithFile_ReturnsRawContent(t *testing.T) {
	isolate(t)

	os.MkdirAll(inboxDir, 0o755)
	os.WriteFile(InboxPath, []byte(`{"type":"text","value":"start"}`), 0o644)

	if got := ReadInbox(); got != `{"type":"text","value":"start"}` {
		t.Fatalf("unexpected content: %s", got)
	}
}

func TestInbox_Consume_MovesInboxToConsumedPath(t *testing.T) {
	isolate(t)

	os.MkdirAll(inboxDir, 0o755)
	os.WriteFile(InboxPath, []byte("{}"), 0o644)

	ConsumeInbox()

	if fileExists(InboxPath) {
		t.Fatal("expected inbox to be moved")
	}
	if !fileExists(InboxConsumedPath) {
		t.Fatal("expected consumed path to exist")
	}
}

func TestInbox_Consume_MissingFile_DoesNotPanic(t *testing.T) {
	isolate(t)

	ConsumeInbox()

	if fileExists(InboxPath) {
		t.Fatal("expected inbox to not exist")
	}
}
