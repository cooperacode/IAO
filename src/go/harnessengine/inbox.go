package harnessengine

import (
	"fmt"
	"os"
)

// File-based input channel — an alternative to argv for the turn's envelope.
//
// The single-quoted argument transport (`./run-development.sh '<JSON>'`) has a structural
// flaw: if the LLM driver forgets the closing quote, the shell enters continuation mode and
// hangs BEFORE the binary runs — no engine validation can catch it. The inbox takes the
// payload out of shell quoting syntax: the agent writes the JSON here with its file-write
// tool (no shell involved) and runs the script with NO arguments, a bare command that can
// never be left unterminated.
const (
	inboxDir = ".harness"

	// InboxPath is the file the driver writes the pending envelope to.
	InboxPath = ".harness/inbox.json"

	// InboxConsumedPath is the trail of the last consumed envelope — avoids reprocessing a
	// stale JSON if the script runs twice without a rewrite, and serves as a diagnostic.
	InboxConsumedPath = ".harness/inbox.consumed.json"
)

// ReadInbox returns the inbox's raw content, or "" if it doesn't exist. Parsing/sanitizing
// stays in Envelope.
func ReadInbox() string {
	if !fileExists(InboxPath) {
		return ""
	}
	data, err := os.ReadFile(InboxPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[Inbox] failed to read %s: %s\n", InboxPath, err)
		return ""
	}
	return string(data)
}

// ConsumeInbox moves the consumed inbox to InboxConsumedPath after a successful parse.
func ConsumeInbox() {
	if !fileExists(InboxPath) {
		return
	}
	if err := ensureDir(inboxDir); err != nil {
		fmt.Fprintf(os.Stderr, "[Inbox] failed to consume %s: %s\n", InboxPath, err)
		return
	}
	if err := os.Rename(InboxPath, InboxConsumedPath); err != nil {
		fmt.Fprintf(os.Stderr, "[Inbox] failed to consume %s: %s\n", InboxPath, err)
	}
}
