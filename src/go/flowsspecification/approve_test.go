package main

import (
	"os"
	"strings"
	"testing"
)

// acceptValidBundleForApprove writes a structurally-complete accepted idea/prd/srs/sdd/
// readiness chain directly to the store (bypassing discover/product/analysis/design/review)
// and a matching "approved" approval.proposal.json, so approve() can be exercised in
// isolation against the development-readiness gate and the publish step. When
// blockingOpenQuestion is true, the PRD carries one unresolved blocking open question — the
// one predicate developmentReady() checks that validateReadiness (reused inside it) does not.
func acceptValidBundleForApprove(blockingOpenQuestion bool) {
	idea := Idea{
		Schema: "iao/idea/v1", Title: "title", Problem: "problem",
		Users: []string{"user"}, DesiredOutcomes: []string{"outcome"},
	}
	writeAccepted("idea", idea)

	prd := PRD{
		Schema:         "iao/prd/v1",
		IdeaDigest:     "sha256:idea",
		Vision:         "vision",
		Goals:          []Goal{{Id: "G-1", Statement: "goal"}},
		NonGoals:       []string{"non-goal"},
		Scope:          []string{"scope"},
		SuccessMetrics: []Metric{{Id: "M-1", GoalId: "G-1", Measure: "measure", Target: "target"}},
	}
	if blockingOpenQuestion {
		prd.OpenQuestions = []OQ{{Id: "OQ-1", Question: "unresolved?", Blocking: true}}
	}
	writeAccepted("prd", prd)

	srs := SRS{
		Schema:    "iao/srs/v1",
		PrdDigest: "sha256:prd",
		FunctionalRequirements: []Req{
			{Id: "RF-1", GoalIds: []string{"G-1"}, Statement: "does the thing", AcceptanceIds: []string{"AC-1"}},
		},
		AcceptanceCriteria: []AC{{Id: "AC-1", RequirementIds: []string{"RF-1"}, Given: "given", When: "when", Then: "then"}},
		Interfaces:         []Iface{{Id: "IF-1", RequirementIds: []string{"RF-1"}, Name: "API", Description: "HTTP contract"}},
		DataRules:          []Rule{{Id: "RN-1", RequirementIds: []string{"RF-1"}, Rule: "non-empty"}},
		Delivery:           Delivery{Target: "target", VerificationStrategy: "strategy"},
	}
	writeAccepted("srs", srs)

	sdd := SDD{
		Schema:    "iao/sdd/v1",
		SrsDigest: "sha256:srs",
		Adrs:      []ADR{{Id: "ADR-1", Title: "title", Decision: "decision", Rationale: "rationale", RequirementIds: []string{"RF-1"}}},
		Controls:  []Control{{Id: "IC-1", RequirementIds: []string{"RF-1"}, Name: "input", Description: "reject invalid input"}},
	}
	writeAccepted("sdd", sdd)

	verdict := Verdict{
		Verdict:   "READY",
		Slices:    []Slice{{Id: "SL-1", RequirementIds: []string{"RF-1"}, AdrIds: []string{"ADR-1"}}},
		Conflicts: []string{},
		Residuals: []string{},
	}
	writeAccepted("readiness", verdict)

	approval := Approval{
		Decision:     "approved",
		BundleDigest: bundleDigest(),
		Rationale:    "reviewed and looks right",
		ApprovedBy:   "human",
		DecidedAt:    "2026-01-01T00:00:00Z",
	}
	writeJSON("approval.proposal.json", approval)
}

func TestApprove_BlockingOpenQuestion_InvokesDevelopmentReadyAndBlocksPublish(t *testing.T) {
	chdirTemp(t)
	acceptValidBundleForApprove(true)
	saveRun(run{Status: "awaiting_approval", Phase: "approve", Counters: map[string]int{}})

	result := approve(nil)

	if result != "stop" {
		t.Fatalf("expected 'stop', got %q", result)
	}
	r := loadRun()
	if r.Status != "publish_blocked" {
		t.Fatalf("expected status 'publish_blocked', got %q", r.Status)
	}
	if r.TerminalReason == nil || !strings.Contains(*r.TerminalReason, "DEV_READINESS_BLOCKING_QUESTION_OPEN") {
		t.Fatalf("expected TerminalReason to report DEV_READINESS_BLOCKING_QUESTION_OPEN, got %v", r.TerminalReason)
	}

	// The gate must run BEFORE publishDocuments — specs/active is never touched.
	if _, err := os.Stat("specs/active"); err == nil {
		t.Fatal("expected specs/active to not exist; developmentReady gate should block before publish")
	}
}

func TestApprove_ValidBundle_PassesDevelopmentReadyAndPublishes(t *testing.T) {
	chdirTemp(t)
	acceptValidBundleForApprove(false)
	saveRun(run{Status: "awaiting_approval", Phase: "approve", Counters: map[string]int{}})

	result := approve(nil)

	if result != "stop" {
		t.Fatalf("expected 'stop', got %q", result)
	}
	r := loadRun()
	if r.Status != "completed" {
		t.Fatalf("expected status 'completed', got %q (reason: %v)", r.Status, r.TerminalReason)
	}
	for _, name := range publishedFiles {
		if _, err := os.Stat("specs/active/" + name); err != nil {
			t.Fatalf("expected '%s' to be published: %v", name, err)
		}
	}
	plan, err := os.ReadFile("specs/active/40-development-plan.json")
	if err != nil {
		t.Fatalf("expected development plan to be published: %v", err)
	}
	for _, reference := range []string{"AC-1", "IF-1", "RN-1", "IC-1"} {
		if !strings.Contains(string(plan), `"`+reference+`"`) {
			t.Fatalf("expected %s in development plan: %s", reference, plan)
		}
	}
}

func TestApprove_MissingAcceptedDocument_BlocksBeforePublishing(t *testing.T) {
	chdirTemp(t)

	// Same as acceptValidBundleForApprove, but the SDD is never accepted.
	prd := PRD{
		Schema: "iao/prd/v1", IdeaDigest: "sha256:idea", Vision: "vision",
		Goals: []Goal{{Id: "G-1", Statement: "goal"}}, NonGoals: []string{"non-goal"}, Scope: []string{"scope"},
		SuccessMetrics: []Metric{{Id: "M-1", GoalId: "G-1", Measure: "measure", Target: "target"}},
	}
	writeAccepted("prd", prd)
	srs := SRS{Schema: "iao/srs/v1", PrdDigest: "sha256:prd", Delivery: Delivery{Target: "t", VerificationStrategy: "v"}}
	writeAccepted("srs", srs)
	verdict := Verdict{Verdict: "FAIL:product", Conflicts: []string{}, Residuals: []string{}}
	writeAccepted("readiness", verdict)

	approval := Approval{
		Decision:     "approved",
		BundleDigest: bundleDigest(),
		Rationale:    "reviewed",
		ApprovedBy:   "human",
		DecidedAt:    "2026-01-01T00:00:00Z",
	}
	writeJSON("approval.proposal.json", approval)
	saveRun(run{Status: "awaiting_approval", Phase: "approve", Counters: map[string]int{}})

	result := approve(nil)

	if result != "stop" {
		t.Fatalf("expected 'stop', got %q", result)
	}
	r := loadRun()
	if r.Status != "publish_blocked" {
		t.Fatalf("expected status 'publish_blocked', got %q", r.Status)
	}
	if r.TerminalReason == nil || !strings.Contains(*r.TerminalReason, "missing") {
		t.Fatalf("expected TerminalReason to mention the missing accepted document, got %v", r.TerminalReason)
	}
	if _, err := os.Stat("specs/active"); err == nil {
		t.Fatal("expected specs/active to not exist when an accepted document is missing")
	}
}

func TestApprove_Revise_RoutesBackToReviewInProgress(t *testing.T) {
	chdirTemp(t)
	acceptValidBundleForApprove(false)

	approval := Approval{
		Decision:     "revise",
		BundleDigest: bundleDigest(),
		Rationale:    "needs another pass",
		ApprovedBy:   "human",
		DecidedAt:    "2026-01-01T00:00:00Z",
	}
	writeJSON("approval.proposal.json", approval)
	saveRun(run{Status: "awaiting_approval", Phase: "approve", Counters: map[string]int{}})

	approve(nil)

	r := loadRun()
	if r.Status != "in_progress" {
		t.Fatalf("expected status 'in_progress' after a revise decision, got %q", r.Status)
	}
	if r.Phase != "review" {
		t.Fatalf("expected phase 'review' after a revise decision, got %q", r.Phase)
	}
}
