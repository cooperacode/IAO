package main

import "testing"

// Regression coverage for a real production crash in the .NET port that this Go port mirrors:
// a DataRule with an empty/null "rule" used to sail through SRS evaluation, get accepted, and
// only break much later — as a NullReferenceException inside the .NET renderer's Escape() —
// when `approve` tried to publish it. The evaluator is the gate that's supposed to reject a
// structurally-invalid document before it is ever accepted, so an empty required text field
// belongs here. (Go's own renderer, escapeMarkdown, was already safe against this: a Go
// `string` field can never be nil, and encoding/json leaves it at its zero value "" instead of
// erroring when the source JSON has that field as `null` — so only the evaluator gap needed
// closing here, not a renderer crash.)

func validSrsDocument() SRS {
	return SRS{
		Schema:     "iao/srs/v1",
		PrdDigest:  "sha256:prd",
		FunctionalRequirements: []Req{
			{Id: "RF-001", GoalIds: []string{"OBJ-001"}, Statement: "does the thing", AcceptanceIds: []string{"AC-001"}},
		},
		AcceptanceCriteria: []AC{
			{Id: "AC-001", RequirementIds: []string{"RF-001"}, Given: "given", When: "when", Then: "then"},
		},
		Delivery: Delivery{Target: "target", VerificationStrategy: "strategy", IsBootstrap: true},
	}
}

func hasCode(violations []violation, code string) bool {
	for _, v := range violations {
		if v.Code == code {
			return true
		}
	}
	return false
}

func TestValidateSrs_DocumentoValido_Passa(t *testing.T) {
	violations := validateSrs(validSrsDocument(), "sha256:prd", []string{"OBJ-001"})

	if len(violations) != 0 {
		t.Fatalf("expected no violations, got %+v", violations)
	}
}

func TestValidateSrs_RegraDeDadosSemTexto_EhRejeitada(t *testing.T) {
	document := validSrsDocument()
	document.DataRules = []Rule{{Id: "DR-001", RequirementIds: []string{"RF-001"}, Rule: ""}}

	violations := validateSrs(document, "sha256:prd", []string{"OBJ-001"})

	if !hasCode(violations, "SRS_DATA_RULE_TEXT_MISSING") {
		t.Fatalf("expected SRS_DATA_RULE_TEXT_MISSING, got %+v", violations)
	}
}

func TestValidateSrs_CamposDeTextoVaziosEmOutrosRegistros_SaoRejeitados(t *testing.T) {
	document := validSrsDocument()
	document.FunctionalRequirements[0].Statement = "   "
	document.AcceptanceCriteria[0].Given = ""
	document.Interfaces = []Iface{{Id: "IF-001", RequirementIds: []string{"RF-001"}, Name: "name", Description: ""}}
	document.Delivery = Delivery{Target: "", VerificationStrategy: "strategy", IsBootstrap: true}

	violations := validateSrs(document, "sha256:prd", []string{"OBJ-001"})

	for _, code := range []string{
		"SRS_REQUIREMENT_STATEMENT_MISSING",
		"SRS_ACCEPTANCE_CRITERION_TEXT_MISSING",
		"SRS_INTERFACE_TEXT_MISSING",
		"SRS_DELIVERY_TEXT_MISSING",
	} {
		if !hasCode(violations, code) {
			t.Fatalf("expected %s among violations, got %+v", code, violations)
		}
	}
}

// renderSRS itself was already null-safe (see comment above), but assert it explicitly so a
// future change to escapeMarkdown can't silently regress this without a test failing.
func TestRenderSrs_RegraDeDadosComTextoVazio_NaoEntraEmPanico(t *testing.T) {
	defer func() {
		if r := recover(); r != nil {
			t.Fatalf("renderSRS panicked: %v", r)
		}
	}()

	document := validSrsDocument()
	document.DataRules = []Rule{{Id: "DR-001", RequirementIds: []string{"RF-001"}, Rule: ""}}

	rendered := renderSRS(document)
	if rendered == "" {
		t.Fatal("expected non-empty rendered output")
	}
}
