package harnessengine

import "testing"

func featureWithCoverage(id int, title string, priority int) Feature {
	return Feature{
		Id: id, Title: title, Priority: priority, Passes: false,
		DependsOn: []int{}, References: []string{"RF-001"},
		ImplementationContext: ImplementationContext{Acceptance: []string{"returns HTTP 401"}},
	}
}

func planRevision(observationId string, features ...Feature) PlanRevision {
	return PlanRevision{
		Reason:         "new evidence requires a global dependency",
		Alternatives:   []string{"keep the stub", "introduce the dependency; selected because it preserves behavior"},
		Features:       features,
		ObservationIds: []string{observationId},
	}
}

func TestEvaluatePlanRevision_ApprovesTrackedChangeThatPreservesCoverage(t *testing.T) {
	isolate(t)
	observation := AppendPlanObservation("verification_failure", intPtr(2), "API verification failed repeatedly.", "exit 1")

	current := []Feature{featureWithCoverage(1, "API", 2)}
	api := featureWithCoverage(1, "API", 2)
	api.DependsOn = []int{2}
	revision := planRevision(observation.Id, api, Feature{Id: 2, Title: "Authentication", Priority: 1})

	result := EvaluatePlanRevision(current, revision, LoadPlanObservations(), 10, 80, 8)

	if result.Verdict != PlanRevisionApprove {
		t.Fatalf("expected approve, got %s; errors=%+v", result.Verdict, result.Errors)
	}
	if len(result.Errors) != 0 {
		t.Fatalf("expected no errors, got %+v", result.Errors)
	}
	if len(result.Diff.Added) != 1 || result.Diff.Added[0] != 2 {
		t.Fatalf("unexpected added: %+v", result.Diff.Added)
	}
	if len(result.Diff.Modified) != 1 || result.Diff.Modified[0] != 1 {
		t.Fatalf("unexpected modified: %+v", result.Diff.Modified)
	}
}

func TestEvaluatePlanRevision_RejectsLostReferenceAndAcceptance(t *testing.T) {
	isolate(t)
	observation := AppendPlanObservation("verification_failure", intPtr(2), "API verification failed repeatedly.", "exit 1")

	current := []Feature{featureWithCoverage(1, "API", 2)}
	revision := planRevision(observation.Id, Feature{Id: 1, Title: "API reduced", Priority: 2})

	result := EvaluatePlanRevision(current, revision, LoadPlanObservations(), 10, 80, 8)

	if result.Verdict != PlanRevisionReject {
		t.Fatalf("expected reject, got %s", result.Verdict)
	}
	if !hasIssueCode(result.Errors, "REQUIREMENT_COVERAGE_REMOVED") {
		t.Fatalf("expected REQUIREMENT_COVERAGE_REMOVED, got %+v", result.Errors)
	}
	if !hasIssueCode(result.Errors, "ACCEPTANCE_REMOVED") {
		t.Fatalf("expected ACCEPTANCE_REMOVED, got %+v", result.Errors)
	}
}

func TestEvaluatePlanRevision_RejectsUnknownObservationAndUnchangedPlan(t *testing.T) {
	isolate(t)
	AppendPlanObservation("verification_failure", intPtr(2), "API verification failed repeatedly.", "exit 1")

	current := []Feature{{Id: 1, Title: "API", Priority: 1}}
	revision := PlanRevision{Reason: "retry", Alternatives: []string{"A", "B"}, Features: current, ObservationIds: []string{"OBS-999"}}

	result := EvaluatePlanRevision(current, revision, LoadPlanObservations(), 10, 80, 8)

	if !hasIssueCode(result.Errors, "OBSERVATION_UNKNOWN") {
		t.Fatalf("expected OBSERVATION_UNKNOWN, got %+v", result.Errors)
	}
	if !hasIssueCode(result.Errors, "PLAN_UNCHANGED") {
		t.Fatalf("expected PLAN_UNCHANGED, got %+v", result.Errors)
	}
}

func TestEvaluatePlanRevision_ApprovesWithWarningWhenBudgetMayBeInsufficient(t *testing.T) {
	isolate(t)
	observation := AppendPlanObservation("verification_failure", intPtr(1), "note", "exit 1")

	current := []Feature{{Id: 1, Title: "API", Priority: 1}}
	revision := planRevision(observation.Id, Feature{Id: 1, Title: "API", Priority: 2}, Feature{Id: 2, Title: "Auth", Priority: 1})

	result := EvaluatePlanRevision(current, revision, LoadPlanObservations(), 10, 4, 8)

	if result.Verdict != PlanRevisionApproveWithWarnings {
		t.Fatalf("expected approve-with-warnings, got %s; errors=%+v", result.Verdict, result.Errors)
	}
	if !hasIssueCode(result.Warnings, "BUDGET_RISK") {
		t.Fatalf("expected BUDGET_RISK warning, got %+v", result.Warnings)
	}
}

func TestEvaluatePlanRevision_RejectsAlreadyAppliedPlan(t *testing.T) {
	isolate(t)
	observation := AppendPlanObservation("verification_failure", intPtr(2), "note", "exit 1")

	current := []Feature{{Id: 1, Title: "API", Priority: 2}}
	api := Feature{Id: 1, Title: "API", Priority: 2, DependsOn: []int{2}}
	revision := planRevision(observation.Id, api, Feature{Id: 2, Title: "Auth", Priority: 1})

	first := EvaluatePlanRevision(current, revision, LoadPlanObservations(), 10, 80, 8)
	RecordPlanRevision(revision, revision.Features, first)

	repeated := EvaluatePlanRevision(current, revision, LoadPlanObservations(), 10, 80, 8)

	if !hasIssueCode(repeated.Errors, "PLAN_REPEATED") {
		t.Fatalf("expected PLAN_REPEATED, got %+v", repeated.Errors)
	}
}

func intPtr(v int) *int { return &v }

func hasIssueCode(issues []PlanRevisionIssue, code string) bool {
	for _, i := range issues {
		if i.Code == code {
			return true
		}
	}
	return false
}
