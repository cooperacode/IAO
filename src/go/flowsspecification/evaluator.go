package main

import (
	"fmt"
	"strings"
)

type violation struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

func formatViolations(values []violation) []string {
	result := make([]string, 0, len(values))
	for _, value := range values {
		result = append(result, value.Code+": "+value.Message)
	}
	return result
}

func failed(code, message string) violation { return violation{Code: code, Message: message} }

func duplicateIDs(ids []string, code, kind string) []violation {
	counts := map[string]int{}
	for _, id := range ids {
		counts[id]++
	}
	var result []violation
	for id, count := range counts {
		if count > 1 {
			result = append(result, failed(code, fmt.Sprintf("duplicate %s id '%s'", kind, id)))
		}
	}
	return result
}

func validateIdea(document Idea, source, sourceDigest string) []violation {
	var result []violation
	if document.Schema != "iao/idea/v1" {
		result = append(result, failed("IDEA_SCHEMA_UNKNOWN", "expected schema 'iao/idea/v1'"))
	}
	if strings.TrimSpace(document.Title) == "" {
		result = append(result, failed("IDEA_TITLE_MISSING", "title is required"))
	}
	if strings.TrimSpace(document.Problem) == "" {
		result = append(result, failed("IDEA_PROBLEM_MISSING", "problem is required"))
	}
	if len(document.Users) == 0 {
		result = append(result, failed("IDEA_USERS_MISSING", "at least one user is required"))
	}
	if len(document.DesiredOutcomes) == 0 {
		result = append(result, failed("IDEA_OUTCOMES_MISSING", "at least one desired outcome is required"))
	}
	ids := make([]string, 0, len(document.OpenQuestions))
	for _, question := range document.OpenQuestions {
		ids = append(ids, question.Id)
	}
	result = append(result, duplicateIDs(ids, "IDEA_OPEN_QUESTION_DUPLICATE_ID", "open question")...)
	if len(canon(document)) > 20_000 {
		result = append(result, failed("IDEA_TOO_LARGE", "idea exceeds the 20000-byte UTF-8 limit"))
	}
	if strings.TrimSpace(source) == "" {
		result = append(result, failed("IDEA_SOURCE_MISSING", "source is required"))
	}
	if sourceDigest == "" {
		result = append(result, failed("IDEA_DIGEST_MISSING", "source digest is required"))
	}
	return result
}

func validatePrd(document PRD, ideaDigest string) []violation {
	var result []violation
	if document.Schema != "iao/prd/v1" {
		result = append(result, failed("PRD_SCHEMA_UNKNOWN", "expected schema 'iao/prd/v1'"))
	}
	if ideaDigest == "" || document.IdeaDigest != ideaDigest {
		result = append(result, failed("PRD_IDEA_DIGEST_STALE", "PRD idea digest does not match the current accepted idea"))
	}
	for _, group := range []struct {
		ids        []string
		code, kind string
	}{
		{goalIDs(document.Goals), "PRD_GOAL_ID_DUPLICATE", "goal"},
		{metricIDs(document.SuccessMetrics), "PRD_METRIC_ID_DUPLICATE", "metric"},
		{riskIDs(document.Risks), "PRD_RISK_ID_DUPLICATE", "risk"},
		{decisionIDs(document.Decisions), "PRD_DECISION_ID_DUPLICATE", "decision"},
	} {
		result = append(result, duplicateIDs(group.ids, group.code, group.kind)...)
	}
	if len(document.Goals) == 0 {
		result = append(result, failed("PRD_GOALS_EMPTY", "at least one goal is required"))
	}
	if len(document.NonGoals) == 0 {
		result = append(result, failed("PRD_NON_GOALS_EMPTY", "at least one non-goal is required"))
	}
	if len(document.Scope) == 0 {
		result = append(result, failed("PRD_SCOPE_EMPTY", "at least one scope entry is required"))
	}
	if len(document.SuccessMetrics) == 0 {
		result = append(result, failed("PRD_METRICS_EMPTY", "at least one success metric is required"))
	}
	for _, metric := range document.SuccessMetrics {
		if strings.TrimSpace(metric.Measure) == "" || strings.TrimSpace(metric.Target) == "" || strings.EqualFold(strings.TrimSpace(metric.Measure), strings.TrimSpace(metric.Target)) {
			result = append(result, failed("PRD_METRIC_MEASURE_TARGET_NOT_SEPARATE", fmt.Sprintf("metric '%s' must have a distinct measure and target", metric.Id)))
		}
	}
	for _, question := range document.OpenQuestions {
		if question.Blocking {
			result = append(result, failed("PRD_OPEN_QUESTION_BLOCKING", "unresolved blocking open question"))
			break
		}
	}
	return result
}

func validateSrs(document SRS, prdDigest string, goals []string) []violation {
	var result []violation
	if document.Schema != "iao/srs/v1" {
		result = append(result, failed("SRS_SCHEMA_UNKNOWN", "expected schema 'iao/srs/v1'"))
	}
	if prdDigest == "" || document.PrdDigest != prdDigest {
		result = append(result, failed("SRS_PRD_DIGEST_STALE", "SRS PRD digest is stale"))
	}
	requirements := append(append([]Req{}, document.FunctionalRequirements...), document.QualityRequirements...)
	requirementIDs := map[string]bool{}
	for _, req := range requirements {
		requirementIDs[req.Id] = true
	}
	result = append(result, duplicateIDs(reqIDs(requirements), "SRS_REQUIREMENT_ID_DUPLICATE", "requirement")...)
	coveredGoals := map[string]bool{}
	acceptanceIDs := map[string]bool{}
	for _, req := range requirements {
		for _, goal := range req.GoalIds {
			coveredGoals[goal] = true
		}
	}
	for _, criterion := range document.AcceptanceCriteria {
		acceptanceIDs[criterion.Id] = true
	}
	for _, goal := range goals {
		if !coveredGoals[goal] {
			result = append(result, failed("SRS_GOAL_NOT_COVERED", fmt.Sprintf("goal '%s' is not covered", goal)))
		}
	}
	for _, req := range requirements {
		if strings.TrimSpace(req.Statement) == "" {
			result = append(result, failed("SRS_REQUIREMENT_STATEMENT_MISSING", fmt.Sprintf("requirement '%s' has no statement", req.Id)))
		}
		if len(req.AcceptanceIds) == 0 {
			result = append(result, failed("SRS_REQUIREMENT_WITHOUT_ACCEPTANCE", fmt.Sprintf("requirement '%s' has no acceptance criterion", req.Id)))
		}
		for _, id := range req.AcceptanceIds {
			if !acceptanceIDs[id] {
				result = append(result, failed("SRS_ACCEPTANCE_REFERENCE_DANGLING", fmt.Sprintf("requirement '%s' references unknown acceptance criterion '%s'", req.Id, id)))
			}
		}
		for _, id := range req.DependsOn {
			if !requirementIDs[id] {
				result = append(result, failed("SRS_REQUIREMENT_DEPENDENCY_DANGLING", fmt.Sprintf("requirement '%s' depends on unknown requirement '%s'", req.Id, id)))
			}
		}
	}
	for _, criterion := range document.AcceptanceCriteria {
		if strings.TrimSpace(criterion.Given) == "" || strings.TrimSpace(criterion.When) == "" || strings.TrimSpace(criterion.Then) == "" {
			result = append(result, failed("SRS_ACCEPTANCE_CRITERION_TEXT_MISSING", fmt.Sprintf("acceptance criterion '%s' has an empty given/when/then", criterion.Id)))
		}
		for _, id := range criterion.RequirementIds {
			if !requirementIDs[id] {
				result = append(result, failed("SRS_ACCEPTANCE_CRITERION_REQUIREMENT_DANGLING", "acceptance criterion references unknown requirement"))
			}
		}
	}
	for _, iface := range document.Interfaces {
		if strings.TrimSpace(iface.Name) == "" || strings.TrimSpace(iface.Description) == "" {
			result = append(result, failed("SRS_INTERFACE_TEXT_MISSING", fmt.Sprintf("interface '%s' has an empty name or description", iface.Id)))
		}
		for _, id := range iface.RequirementIds {
			if !requirementIDs[id] {
				result = append(result, failed("SRS_INTERFACE_REQUIREMENT_DANGLING", "interface references unknown requirement"))
			}
		}
	}
	for _, rule := range document.DataRules {
		if strings.TrimSpace(rule.Rule) == "" {
			result = append(result, failed("SRS_DATA_RULE_TEXT_MISSING", fmt.Sprintf("data rule '%s' has no rule text", rule.Id)))
		}
		for _, id := range rule.RequirementIds {
			if !requirementIDs[id] {
				result = append(result, failed("SRS_DATA_RULE_REQUIREMENT_DANGLING", "data rule references unknown requirement"))
			}
		}
	}
	if strings.TrimSpace(document.Delivery.Target) == "" || strings.TrimSpace(document.Delivery.VerificationStrategy) == "" {
		result = append(result, failed("SRS_DELIVERY_TEXT_MISSING", "delivery contract has an empty target or verification strategy"))
	}
	return result
}

const maxDesignContentUTF8Bytes = 1_000_000

func sameStrings(left, right []string) bool {
	if len(left) != len(right) {
		return false
	}
	for i := range left {
		if left[i] != right[i] {
			return false
		}
	}
	return true
}

func validateSdd(document SDD, srsDigest string, requirementIDs []string) []violation {
	return validateSddWithSources(document, srsDigest, requirementIDs, "", nil)
}

func validateSddWithSources(document SDD, srsDigest string, requirementIDs []string, currentSourceDigest string, currentSourceFiles []string) []violation {
	var result []violation
	if document.Schema != "iao/sdd/v1" {
		result = append(result, failed("SDD_SCHEMA_UNKNOWN", "expected schema 'iao/sdd/v1'"))
	}
	if srsDigest == "" || document.SrsDigest != srsDigest {
		result = append(result, failed("SDD_SRS_DIGEST_STALE", "SDD SRS digest is stale"))
	}
	result = append(result, duplicateIDs(adrIDs(document.Adrs), "SDD_ADR_ID_DUPLICATE", "ADR")...)
	known := map[string]bool{}
	allocated := map[string]bool{}
	for _, id := range requirementIDs {
		known[id] = true
	}
	for _, adr := range document.Adrs {
		for _, id := range adr.RequirementIds {
			allocated[id] = true
			if !known[id] {
				result = append(result, failed("SDD_ADR_REQUIREMENT_DANGLING", "ADR references unknown requirement"))
			}
		}
	}
	for _, id := range requirementIDs {
		if !allocated[id] {
			result = append(result, failed("SDD_REQUIREMENT_NOT_ALLOCATED", fmt.Sprintf("requirement '%s' is not allocated", id)))
		}
	}
	for _, control := range document.Controls {
		for _, id := range control.RequirementIds {
			if !known[id] {
				result = append(result, failed("SDD_CONTROL_REQUIREMENT_DANGLING", "control references unknown requirement"))
			}
		}
	}
	if document.DesignContent != "" && len([]byte(document.DesignContent)) > maxDesignContentUTF8Bytes {
		result = append(result, failed("SDD_DESIGN_CONTENT_TOO_LARGE", "designContent exceeds the 1000000-byte UTF-8 limit"))
	}
	if strings.TrimSpace(currentSourceDigest) != "" {
		if strings.TrimSpace(document.SourceDigest) == "" {
			result = append(result, failed("SDD_SOURCE_DIGEST_MISSING", "sourceDigest is required when an accepted source bundle exists"))
		} else if document.SourceDigest != currentSourceDigest {
			result = append(result, failed("SDD_SOURCE_DIGEST_STALE", "SDD source digest is stale"))
		}
		if len(document.SourceFiles) == 0 {
			result = append(result, failed("SDD_SOURCE_FILES_MISSING", "sourceFiles is required when an accepted source bundle exists"))
		} else if !sameStrings(document.SourceFiles, currentSourceFiles) {
			result = append(result, failed("SDD_SOURCE_FILES_STALE", "SDD source files do not match the current accepted source bundle"))
		}
		if strings.TrimSpace(document.DesignContent) == "" {
			result = append(result, failed("SDD_DESIGN_CONTENT_MISSING", "designContent is required when an accepted source bundle exists"))
		}
	}
	return result
}

func validateReadiness(document Verdict, requirementIDs, adrIDs []string) []violation {
	var result []violation
	if document.Verdict != "READY" && document.Verdict != "FAIL:product" && document.Verdict != "FAIL:analysis" && document.Verdict != "FAIL:design" {
		result = append(result, failed("READINESS_VERDICT_INVALID", "invalid readiness verdict"))
	}
	if document.Conflicts == nil {
		result = append(result, failed("READINESS_CONFLICTS_MISSING", "conflicts must be present"))
	}
	if document.Residuals == nil {
		result = append(result, failed("READINESS_RESIDUALS_MISSING", "residuals must be present"))
	}
	if document.Verdict != "READY" {
		return result
	}
	if len(document.Slices) == 0 {
		result = append(result, failed("READINESS_SLICES_EMPTY", "READY requires at least one slice"))
	}
	if len(document.Slices) > 10 {
		result = append(result, failed("READINESS_TOO_MANY_SLICES", "readiness has more than ten slices"))
	}
	result = append(result, duplicateIDs(sliceIDs(document.Slices), "READINESS_SLICE_ID_DUPLICATE", "readiness slice")...)
	knownReq, knownAdr, knownSlice := boolSet(requirementIDs), boolSet(adrIDs), boolSet(sliceIDs(document.Slices))
	for _, slice := range document.Slices {
		for _, id := range slice.RequirementIds {
			if !knownReq[id] {
				result = append(result, failed("READINESS_REQUIREMENT_REFERENCE_DANGLING", "slice references unknown requirement"))
			}
		}
		for _, id := range slice.AdrIds {
			if !knownAdr[id] {
				result = append(result, failed("READINESS_ADR_REFERENCE_DANGLING", "slice references unknown ADR"))
			}
		}
		for _, id := range slice.DependsOn {
			if id == slice.Id || !knownSlice[id] {
				result = append(result, failed("READINESS_DEPENDENCY_REFERENCE_DANGLING", "slice dependency is dangling"))
			}
		}
	}
	if hasCycle(document.Slices) {
		result = append(result, failed("READINESS_DEPENDENCY_CYCLE", "slice dependency graph contains a cycle"))
	}
	initial := false
	covered := map[string]bool{}
	for _, slice := range document.Slices {
		initial = initial || len(slice.DependsOn) == 0
		for _, id := range slice.RequirementIds {
			covered[id] = true
		}
	}
	if len(document.Slices) > 0 && !initial {
		result = append(result, failed("READINESS_NO_INITIAL_SLICE", "at least one slice must have no prerequisite"))
	}
	for _, id := range requirementIDs {
		if !covered[id] {
			result = append(result, failed("READINESS_REQUIREMENT_NOT_SLICED", "requirement is not covered by a slice"))
		}
	}
	return result
}

func validateApproval(document Approval, current string) []violation {
	var result []violation
	if document.Decision != "approved" && document.Decision != "revise" {
		result = append(result, failed("APPROVAL_DECISION_INVALID", "invalid approval decision"))
	}
	if current == "" || document.BundleDigest != current {
		result = append(result, failed("APPROVAL_BUNDLE_DIGEST_STALE", "approval bundle digest is stale"))
	}
	if strings.TrimSpace(document.Rationale) == "" {
		result = append(result, failed("APPROVAL_RATIONALE_MISSING", "rationale is required"))
	}
	return result
}

func boolSet(values []string) map[string]bool {
	result := map[string]bool{}
	for _, value := range values {
		result[value] = true
	}
	return result
}
func goalIDs(values []Goal) []string {
	result := []string{}
	for _, value := range values {
		result = append(result, value.Id)
	}
	return result
}
func metricIDs(values []Metric) []string {
	result := []string{}
	for _, value := range values {
		result = append(result, value.Id)
	}
	return result
}
func riskIDs(values []Risk) []string {
	result := []string{}
	for _, value := range values {
		result = append(result, value.Id)
	}
	return result
}
func decisionIDs(values []Decision) []string {
	result := []string{}
	for _, value := range values {
		result = append(result, value.Id)
	}
	return result
}
func reqIDs(values []Req) []string {
	result := []string{}
	for _, value := range values {
		result = append(result, value.Id)
	}
	return result
}
func adrIDs(values []ADR) []string {
	result := []string{}
	for _, value := range values {
		result = append(result, value.Id)
	}
	return result
}
func sliceIDs(values []Slice) []string {
	result := []string{}
	for _, value := range values {
		result = append(result, value.Id)
	}
	return result
}

func hasCycle(slices []Slice) bool {
	indegree := map[string]int{}
	adjacency := map[string][]string{}
	for _, slice := range slices {
		indegree[slice.Id] = 0
		adjacency[slice.Id] = []string{}
	}
	for _, slice := range slices {
		for _, dependency := range slice.DependsOn {
			if dependency != slice.Id && adjacency[dependency] != nil {
				adjacency[dependency] = append(adjacency[dependency], slice.Id)
				indegree[slice.Id]++
			}
		}
	}
	queue := []string{}
	for id, degree := range indegree {
		if degree == 0 {
			queue = append(queue, id)
		}
	}
	visited := 0
	for len(queue) > 0 {
		current := queue[0]
		queue = queue[1:]
		visited++
		for _, next := range adjacency[current] {
			indegree[next]--
			if indegree[next] == 0 {
				queue = append(queue, next)
			}
		}
	}
	return visited < len(indegree)
}

func developmentReady(prd PRD, srs SRS, sdd SDD, readiness Verdict, approvedDigest, currentDigest string, rendered map[string]string, maxBytes int) []violation {
	var result []violation
	if approvedDigest != currentDigest || currentDigest == "" {
		result = append(result, failed("DEV_READINESS_BUNDLE_DIGEST_STALE", "approval bundle digest is stale"))
	}
	for _, question := range prd.OpenQuestions {
		if question.Blocking {
			result = append(result, failed("DEV_READINESS_BLOCKING_QUESTION_OPEN", "unresolved blocking open question"))
			break
		}
	}
	requirements := reqIDs(append(append([]Req{}, srs.FunctionalRequirements...), srs.QualityRequirements...))
	result = append(result, validateReadiness(readiness, requirements, adrIDs(sdd.Adrs))...)
	total := 0
	for _, content := range rendered {
		total += len([]byte(content))
	}
	if total > maxBytes {
		result = append(result, failed("DEV_READINESS_BUNDLE_TOO_LARGE", "rendered bundle exceeds docsMaxChars"))
	}
	return result
}
