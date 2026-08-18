package main

import (
	"encoding/json"
	"testing"
)

// Regression test for a real, severe bug found while auditing cross-engine compatibility with
// the .NET port: SRS's FunctionalRequirements and QualityRequirements fields used to share a
// single combined declaration with one json tag ("functionalRequirements" for both). Go's
// encoding/json treats that as a tag conflict at the same nesting depth and silently drops
// *both* fields from Marshal/Unmarshal — no error, no panic, just empty slices and a JSON
// document missing both keys entirely. Any SRS document round-tripped through this struct
// (including one produced by the .NET port, which does use the correct distinct
// "functionalRequirements"/"qualityRequirements" keys) lost every functional and quality
// requirement. This asserts the real interoperability contract: both fields must decode from,
// and encode to, their own distinct JSON key.
func TestSRS_FuncionalEQualidadeSaoCamposJsonDistintos(t *testing.T) {
	input := []byte(`{
		"schema": "iao/srs/v1",
		"prdDigest": "sha256:prd",
		"functionalRequirements": [{"id": "RF-001", "goalIds": ["OBJ-001"], "statement": "func req", "acceptanceIds": ["AC-001"]}],
		"qualityRequirements": [{"id": "RNF-001", "goalIds": ["OBJ-002"], "statement": "quality req", "acceptanceIds": ["AC-002"]}]
	}`)

	var document SRS
	if err := json.Unmarshal(input, &document); err != nil {
		t.Fatalf("unmarshal failed: %v", err)
	}

	if len(document.FunctionalRequirements) != 1 || document.FunctionalRequirements[0].Id != "RF-001" {
		t.Fatalf("FunctionalRequirements not decoded correctly: %+v", document.FunctionalRequirements)
	}
	if len(document.QualityRequirements) != 1 || document.QualityRequirements[0].Id != "RNF-001" {
		t.Fatalf("QualityRequirements not decoded correctly: %+v", document.QualityRequirements)
	}

	out, err := json.Marshal(document)
	if err != nil {
		t.Fatalf("marshal failed: %v", err)
	}

	var roundTripped map[string]any
	if err := json.Unmarshal(out, &roundTripped); err != nil {
		t.Fatalf("re-unmarshal of marshaled output failed: %v", err)
	}
	if _, ok := roundTripped["functionalRequirements"]; !ok {
		t.Fatalf("marshaled output is missing \"functionalRequirements\": %s", out)
	}
	if _, ok := roundTripped["qualityRequirements"]; !ok {
		t.Fatalf("marshaled output is missing \"qualityRequirements\": %s", out)
	}
}
