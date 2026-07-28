package main

import (
	"crypto/rand"
	"fmt"
)

// newRunId returns a random UUIDv4-shaped identifier for RunConfig.RunId (RFC §6.4) — the
// harness only needs a globally unique run identity, not RFC 4122 conformance beyond shape.
func newRunId() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return ""
	}
	b[6] = (b[6] & 0x0f) | 0x40 // version 4
	b[8] = (b[8] & 0x3f) | 0x80 // variant 10

	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}
