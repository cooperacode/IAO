// Package harnessengine is the domain-agnostic core of the harness — the Go port of
// Harness.Engine (.NET), harness_engine (Python/Rust). It owns entry/dispatch, prompt
// formatting, and persistence/telemetry; a flow (e.g. flowsdevelopment) owns domain policy.
package harnessengine

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
)

// EnvelopeType holds the protocol signals carried in Envelope.Type.
var EnvelopeType = struct {
	Text    string
	Tool    string
	Command string
	Error   string
}{
	Text:    "text",
	Tool:    "tool",
	Command: "command",
	Error:   "error",
}

// Envelope is the data contract exchanged between the driver (agent) and the state
// machine. The model returns this envelope as JSON; the engine dispatches by Value.
//
// There is no tokens field: the typical driver is an LLM with no access to its own
// request's usage, so any self-reported count would be confabulated. The cost ceiling
// only uses measures the engine attests itself (steps and instruction chars — see
// TaskRegistry); real tokens live in the caller's billing metadata.
type Envelope struct {
	Type         string            `json:"type"`
	Value        string            `json:"value"`
	Args         []string          `json:"args"`
	Context      map[string]string `json:"context,omitempty"`
	ContextUsage *ContextUsage     `json:"contextUsage,omitempty"`
}

// NewEnvelope builds an envelope with no context (args defaults to an empty, non-nil slice).
func NewEnvelope(envelopeType, value string, args []string) Envelope {
	if args == nil {
		args = []string{}
	}
	return Envelope{Type: envelopeType, Value: value, Args: args}
}

// ToJSON serializes the envelope compactly — the same wire format as .NET/Python/Rust.
func (e Envelope) ToJSON() string {
	b, err := json.Marshal(e)
	if err != nil {
		// Envelope only carries strings/maps of strings; marshaling never fails in practice.
		return "{}"
	}
	return string(b)
}

// ParseEnvelope is a tolerant parse: accepts markdown fences and surrounding text around
// the JSON object.
func ParseEnvelope(value string) *Envelope {
	envelope, err := tryParseEnvelope(value)
	if err != nil {
		// Diagnostics go to stderr — stdout is the harness transport channel (the driver
		// reads stdout as the next instruction) and must not be polluted.
		fmt.Fprintln(os.Stderr, err)
		return nil
	}
	return envelope
}

func tryParseEnvelope(value string) (*Envelope, error) {
	if strings.TrimSpace(value) == "" {
		return nil, fmt.Errorf("The JSON envelope cannot be null or empty.")
	}

	sanitized := sanitizeEnvelope(value)

	var root map[string]json.RawMessage
	if err := json.Unmarshal([]byte(sanitized), &root); err != nil {
		return nil, err
	}

	envType, err := stringField(root, "type")
	if err != nil {
		return nil, err
	}
	envValue, err := stringField(root, "value")
	if err != nil {
		return nil, err
	}

	args := []string{}
	if raw, ok := root["args"]; ok {
		var items []json.RawMessage
		if err := json.Unmarshal(raw, &items); err == nil {
			for _, item := range items {
				var s string
				if err := json.Unmarshal(item, &s); err != nil {
					return nil, fmt.Errorf("cada item de 'args' deve ser uma string.")
				}
				if strings.TrimSpace(s) != "" {
					args = append(args, s)
				}
			}
		}
	}

	var context map[string]string
	if raw, ok := root["context"]; ok {
		var obj map[string]json.RawMessage
		if err := json.Unmarshal(raw, &obj); err == nil {
			context = make(map[string]string, len(obj))
			for k, v := range obj {
				var s string
				if err := json.Unmarshal(v, &s); err != nil {
					return nil, fmt.Errorf("cada valor de 'context' deve ser uma string.")
				}
				context[k] = s
			}
		}
	}

	var contextUsage *ContextUsage
	if raw, ok := root["contextUsage"]; ok {
		var usage ContextUsage
		if err := json.Unmarshal(raw, &usage); err == nil {
			contextUsage = &usage
		}
	}

	return &Envelope{Type: envType, Value: envValue, Args: args, Context: context, ContextUsage: contextUsage}, nil
}

// stringField reads an optional string field: absent/null becomes "", any other type is a
// parse error.
func stringField(root map[string]json.RawMessage, key string) (string, error) {
	raw, ok := root[key]
	if !ok {
		return "", nil
	}
	if string(raw) == "null" {
		return "", nil
	}
	var s string
	if err := json.Unmarshal(raw, &s); err != nil {
		return "", fmt.Errorf("'%s' deve ser uma string.", key)
	}
	return s, nil
}

// sanitizeEnvelope normalizes model output that wraps JSON in markdown fences or adds
// surrounding prose into the raw JSON object.
func sanitizeEnvelope(value string) string {
	v := strings.TrimSpace(value)

	if strings.HasPrefix(v, "```") {
		if idx := strings.IndexByte(v, '\n'); idx >= 0 {
			v = v[idx+1:]
		}
		if idx := strings.LastIndex(v, "```"); idx >= 0 {
			v = v[:idx]
		}
		v = strings.TrimSpace(v)
	}

	start := strings.IndexByte(v, '{')
	end := strings.LastIndexByte(v, '}')
	if start >= 0 && end > start {
		v = v[start : end+1]
	}

	return v
}
