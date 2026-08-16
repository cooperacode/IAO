package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	engine "github.com/cooperacode/IAO/src/go/harnessengine"
)

var publishedFiles = []string{
	"00-prd.md",
	"10-software-requirements-specification.md",
	"20-software-design-document.md",
	"30-readiness-handoff.md",
}

const developmentPlanFilename = "40-development-plan.json"

func expectedPublishedFiles() []string {
	return append(append([]string{}, publishedFiles...), developmentPlanFilename)
}

func developmentPlanJSON(srs SRS, sdd SDD, readiness Verdict, rendered map[string]string) string {
	requirements := map[string]string{}
	for _, requirement := range append(append([]Req{}, srs.FunctionalRequirements...), srs.QualityRequirements...) {
		requirements[requirement.Id] = requirement.Statement
	}
	adrs := map[string]ADR{}
	for _, adr := range sdd.Adrs {
		adrs[adr.Id] = adr
	}
	sliceIDs := map[string]int{}
	for index, slice := range readiness.Slices {
		sliceIDs[slice.Id] = index + 1
	}
	features := make([]engine.Feature, 0, len(readiness.Slices))
	for index, slice := range readiness.Slices {
		requirementIDs := map[string]bool{}
		for _, id := range slice.RequirementIds {
			requirementIDs[id] = true
		}
		linked := func(ids []string) bool {
			for _, id := range ids {
				if requirementIDs[id] {
					return true
				}
			}
			return false
		}
		requirementText := []string{}
		for _, id := range slice.RequirementIds {
			if statement, ok := requirements[id]; ok {
				requirementText = append(requirementText, id+": "+statement)
			} else {
				requirementText = append(requirementText, id)
			}
		}
		acceptanceCriteria := []AC{}
		for _, item := range srs.AcceptanceCriteria {
			if linked(item.RequirementIds) {
				acceptanceCriteria = append(acceptanceCriteria, item)
			}
		}
		interfaces := []Iface{}
		for _, item := range srs.Interfaces {
			if linked(item.RequirementIds) {
				interfaces = append(interfaces, item)
			}
		}
		dataRules := []Rule{}
		for _, item := range srs.DataRules {
			if linked(item.RequirementIds) {
				dataRules = append(dataRules, item)
			}
		}
		controls := []Control{}
		for _, item := range sdd.Controls {
			if linked(item.RequirementIds) {
				controls = append(controls, item)
			}
		}
		decisionText := []string{}
		for _, id := range slice.AdrIds {
			if adr, ok := adrs[id]; ok {
				decisionText = append(decisionText, fmt.Sprintf("%s: %s. Decision: %s. Rationale: %s", id, adr.Title, adr.Decision, adr.Rationale))
			} else {
				decisionText = append(decisionText, id)
			}
		}
		dependsOn := []int{}
		for _, dependency := range slice.DependsOn {
			if id, ok := sliceIDs[dependency]; ok {
				dependsOn = append(dependsOn, id)
			}
		}
		references := append([]string{}, slice.RequirementIds...)
		references = append(references, slice.AdrIds...)
		for _, item := range acceptanceCriteria {
			references = append(references, item.Id)
		}
		for _, item := range interfaces {
			references = append(references, item.Id)
		}
		for _, item := range dataRules {
			references = append(references, item.Id)
		}
		for _, item := range controls {
			references = append(references, item.Id)
		}
		acceptanceText := []string{slice.AcceptanceCriterion}
		for _, item := range acceptanceCriteria {
			acceptanceText = append(acceptanceText, fmt.Sprintf("%s: Given %s. When %s. Then %s", item.Id, item.Given, item.When, item.Then))
		}
		relatedContractText := []string{}
		for _, item := range interfaces {
			relatedContractText = append(relatedContractText, fmt.Sprintf("%s: %s. %s", item.Id, item.Name, item.Description))
		}
		for _, item := range dataRules {
			relatedContractText = append(relatedContractText, fmt.Sprintf("%s: %s", item.Id, item.Rule))
		}
		for _, item := range controls {
			relatedContractText = append(relatedContractText, fmt.Sprintf("%s: %s. %s", item.Id, item.Name, item.Description))
		}
		features = append(features, engine.Feature{
			Id: index + 1, Title: slice.Goal, Priority: index + 1, Passes: false,
			DependsOn: dependsOn, Description: fmt.Sprintf("%s Observable outcome: %s. Happy path: %s. Failure path: %s.", slice.Goal, slice.ObservableOutcome, slice.HappyPath, slice.FailurePath),
			References: uniqueStringsForPlan(references),
			ImplementationContext: engine.ImplementationContext{
				Requirements: requirementText,
				Decisions:    decisionText,
				Constraints:  append(append(prefixValues("out of scope: ", slice.OutOfScope), slice.Contracts...), relatedContractText...),
				Files:        []string{slice.SuggestedTarget}, Acceptance: acceptanceText,
			},
		})
	}
	return string(canon(map[string]any{
		"schema":                    "iao/development-plan/v1",
		"specificationBundleDigest": digestText(strings.Join([]string{rendered[publishedFiles[0]], rendered[publishedFiles[1]], rendered[publishedFiles[2]], rendered[publishedFiles[3]]}, "|")),
		"sourceFiles":               publishedFiles,
		"features":                  features,
		"targetDescription":         srs.Delivery.Target,
		"verificationDescription":   srs.Delivery.VerificationStrategy,
	}))
}

func prefixValues(prefix string, values []string) []string {
	result := make([]string, len(values))
	for i, value := range values {
		result[i] = prefix + value
	}
	return result
}

func uniqueStringsForPlan(values []string) []string {
	seen := map[string]bool{}
	result := []string{}
	for _, value := range values {
		if value != "" && !seen[value] {
			seen[value] = true
			result = append(result, value)
		}
	}
	return result
}

func escapeMarkdown(value string) string {
	var builder strings.Builder
	for _, character := range value {
		if strings.ContainsRune(`\*_`+"`"+`[]<>#|`, character) {
			builder.WriteByte('\\')
		}
		builder.WriteRune(character)
	}
	return builder.String()
}

func joinIDs(values []string) string {
	escaped := make([]string, len(values))
	for index, value := range values {
		escaped[index] = escapeMarkdown(value)
	}
	return strings.Join(escaped, ", ")
}

func bullets[T any](builder *strings.Builder, values []T, render func(T) string, trailingBlank bool) {
	if len(values) == 0 {
		builder.WriteString("_None._\n")
	} else {
		for _, value := range values {
			builder.WriteString(render(value))
			builder.WriteByte('\n')
		}
	}
	if trailingBlank {
		builder.WriteByte('\n')
	}
}

func renderPRD(document PRD) string {
	var builder strings.Builder
	builder.WriteString("# Product Requirements Document\n\n## Vision\n")
	builder.WriteString(escapeMarkdown(document.Vision))
	builder.WriteString("\n\n## Goals\n")
	bullets(&builder, document.Goals, func(goal Goal) string { return fmt.Sprintf("- **%s:** %s", goal.Id, escapeMarkdown(goal.Statement)) }, true)
	builder.WriteString("## Success Metrics\n")
	bullets(&builder, document.SuccessMetrics, func(metric Metric) string {
		return fmt.Sprintf("- **%s** (%s): %s → %s", metric.Id, escapeMarkdown(metric.GoalId), escapeMarkdown(metric.Measure), escapeMarkdown(metric.Target))
	}, true)
	builder.WriteString("## Non-Goals\n")
	bullets(&builder, document.NonGoals, func(value string) string { return "- " + escapeMarkdown(value) }, true)
	builder.WriteString("## Scope\n")
	bullets(&builder, document.Scope, func(value string) string { return "- " + escapeMarkdown(value) }, true)
	builder.WriteString("## Risks\n")
	bullets(&builder, document.Risks, func(risk Risk) string {
		return fmt.Sprintf("- **%s** (%s): %s — mitigation: %s", risk.Id, escapeMarkdown(risk.Severity), escapeMarkdown(risk.Description), escapeMarkdown(risk.Mitigation))
	}, true)
	builder.WriteString("## Decisions\n")
	bullets(&builder, document.Decisions, func(decision Decision) string {
		return fmt.Sprintf("- **%s:** %s — rationale: %s", decision.Id, escapeMarkdown(decision.Statement), escapeMarkdown(decision.Rationale))
	}, true)
	builder.WriteString("## Open Questions\n")
	bullets(&builder, document.OpenQuestions, func(question OQ) string {
		state := "non-blocking"
		if question.Blocking {
			state = "blocking"
		}
		return fmt.Sprintf("- **%s** [%s]: %s", question.Id, state, escapeMarkdown(question.Question))
	}, false)
	return builder.String()
}

func renderSRS(document SRS) string {
	var builder strings.Builder
	builder.WriteString("# Software Requirements Specification\n\n## Functional Requirements\n")
	bullets(&builder, document.FunctionalRequirements, func(req Req) string {
		return fmt.Sprintf("- **%s** [%s]: %s", req.Id, joinIDs(req.GoalIds), escapeMarkdown(req.Statement))
	}, true)
	builder.WriteString("## Quality Requirements\n")
	bullets(&builder, document.QualityRequirements, func(req Req) string {
		return fmt.Sprintf("- **%s** [%s]: %s", req.Id, joinIDs(req.GoalIds), escapeMarkdown(req.Statement))
	}, true)
	builder.WriteString("## Acceptance Criteria\n")
	bullets(&builder, document.AcceptanceCriteria, func(item AC) string {
		return fmt.Sprintf("- **%s** (%s) — Given %s, When %s, Then %s", item.Id, joinIDs(item.RequirementIds), escapeMarkdown(item.Given), escapeMarkdown(item.When), escapeMarkdown(item.Then))
	}, true)
	builder.WriteString("## Interfaces\n")
	bullets(&builder, document.Interfaces, func(item Iface) string {
		return fmt.Sprintf("- **%s** (%s) %s: %s", item.Id, joinIDs(item.RequirementIds), escapeMarkdown(item.Name), escapeMarkdown(item.Description))
	}, true)
	builder.WriteString("## Data Rules\n")
	bullets(&builder, document.DataRules, func(item Rule) string {
		return fmt.Sprintf("- **%s** (%s): %s", item.Id, joinIDs(item.RequirementIds), escapeMarkdown(item.Rule))
	}, true)
	builder.WriteString("## Delivery\n")
	builder.WriteString(fmt.Sprintf("- **Target:** %s\n- **Verification Strategy:** %s\n- **Bootstrap:** %s\n", escapeMarkdown(document.Delivery.Target), escapeMarkdown(document.Delivery.VerificationStrategy), map[bool]string{true: "yes", false: "no"}[document.Delivery.IsBootstrap]))
	return builder.String()
}

func renderSDD(document SDD) string {
	var builder strings.Builder
	builder.WriteString("# Software Design Document\n\n")
	if strings.TrimSpace(document.DesignContent) != "" {
		builder.WriteString(strings.TrimRight(document.DesignContent, "\r\n"))
		builder.WriteString("\n\n")
	}
	builder.WriteString("## Architecture Decision Records\n")
	bullets(&builder, document.Adrs, func(item ADR) string {
		return fmt.Sprintf("- **%s** [%s] %s: %s — rationale: %s", item.Id, joinIDs(item.RequirementIds), escapeMarkdown(item.Title), escapeMarkdown(item.Decision), escapeMarkdown(item.Rationale))
	}, true)
	builder.WriteString("## Controls\n")
	bullets(&builder, document.Controls, func(item Control) string {
		return fmt.Sprintf("- **%s** [%s] %s: %s", item.Id, joinIDs(item.RequirementIds), escapeMarkdown(item.Name), escapeMarkdown(item.Description))
	}, false)
	return builder.String()
}

func renderReadiness(document Verdict) string {
	var builder strings.Builder
	builder.WriteString("# Readiness Handoff\n\n## Verdict\n" + escapeMarkdown(document.Verdict) + "\n\n## Conflicts\n")
	bullets(&builder, document.Conflicts, func(value string) string { return "- " + escapeMarkdown(value) }, true)
	builder.WriteString("## Residuals\n")
	bullets(&builder, document.Residuals, func(value string) string { return "- " + escapeMarkdown(value) }, true)
	builder.WriteString("## Slices\n")
	if len(document.Slices) == 0 {
		builder.WriteString("_None._\n")
		return builder.String()
	}
	for _, slice := range document.Slices {
		builder.WriteString(fmt.Sprintf("### %s — %s\n", escapeMarkdown(slice.Id), escapeMarkdown(slice.Classification)))
		builder.WriteString(fmt.Sprintf("- **Goal:** %s\n- **In Scope:** %s\n- **Out of Scope:** %s\n- **Observable Outcome:** %s\n- **Requirements:** %s\n- **ADRs:** %s\n- **Depends On:** %s\n- **Contracts:** %s\n- **Happy Path:** %s\n- **Failure Path:** %s\n- **Acceptance Criterion:** %s\n- **Suggested Target:** %s\n- **Suggested Verification Strategy:** %s\n\n", escapeMarkdown(slice.Goal), joinIDs(slice.InScope), joinIDs(slice.OutOfScope), escapeMarkdown(slice.ObservableOutcome), joinIDs(slice.RequirementIds), joinIDs(slice.AdrIds), joinIDs(slice.DependsOn), joinIDs(slice.Contracts), escapeMarkdown(slice.HappyPath), escapeMarkdown(slice.FailurePath), escapeMarkdown(slice.AcceptanceCriterion), escapeMarkdown(slice.SuggestedTarget), escapeMarkdown(slice.SuggestedVerificationStrategy)))
	}
	return builder.String()
}

func renderBundle(prd PRD, srs SRS, sdd SDD, readiness Verdict) map[string]string {
	return map[string]string{publishedFiles[0]: renderPRD(prd), publishedFiles[1]: renderSRS(srs), publishedFiles[2]: renderSDD(sdd), publishedFiles[3]: renderReadiness(readiness)}
}

const destinationDir = "specs/active"

// publishDocuments renders, stages, validates and — only if validation passes — publishes
// the four accepted documents, then proves the postcondition via verifyPostcondition before
// reporting success (mirrors SpecificationPublisher.Publish (.NET)). Returns (false, reason)
// with a specific, stable-prefixed message on any block; the destination is left completely
// untouched if the ownership check fails, and never rolled back if only the postcondition
// check fails afterwards — the caller (approve()) turns either into publish_blocked either
// way.
func publishDocuments(rendered map[string]string) (bool, string) {
	if _, ok := rendered[developmentPlanFilename]; !ok {
		rendered[developmentPlanFilename] = `{"schema":"iao/development-plan/v1","features":[]}`
	}
	expectedPublished := expectedPublishedFiles()
	previouslyOwned := map[string]bool{}
	if raw, err := os.ReadFile(path("publish-manifest.json")); err == nil {
		var manifest struct {
			OwnedFiles []string `json:"ownedFiles"`
		}
		_ = json.Unmarshal(raw, &manifest)
		for _, name := range manifest.OwnedFiles {
			previouslyOwned[name] = true
		}
	}

	expected := map[string]bool{}
	for _, name := range expectedPublished {
		expected[name] = true
	}

	// Ownership check BEFORE anything in the destination is touched. An empty/missing
	// previous manifest means nothing is "owned" yet — so even a pre-existing file that
	// happens to share one of the four expected names blocks a first publish, unless the
	// destination is missing or empty (the normal fresh-publish case).
	if entries, err := os.ReadDir(destinationDir); err == nil {
		for _, entry := range entries {
			if !entry.Type().IsRegular() {
				continue
			}
			name := entry.Name()
			if !expected[name] {
				return false, fmt.Sprintf("unrecognized file '%s' exists in '%s'; publish blocked.", name, destinationDir)
			}
			if !previouslyOwned[name] {
				return false, fmt.Sprintf("file '%s' exists in '%s' but is not owned by a previous publish of this flow; publish blocked.", name, destinationDir)
			}
		}
	}

	staging, err := os.MkdirTemp(".", "specification-staging-")
	if err != nil {
		return false, fmt.Sprintf("failed to create staging directory: %s", err)
	}
	defer os.RemoveAll(staging)
	digests := map[string]string{}
	for _, name := range expectedPublished {
		content := rendered[name]
		if err := os.WriteFile(filepath.Join(staging, name), []byte(content), 0644); err != nil {
			return false, fmt.Sprintf("failed to stage '%s': %s", name, err)
		}
		digests[name] = digestText(content)
	}

	// Validation passed: copy the four known files in — never a directory-level
	// delete/glob, only these exact, known filenames — then write the manifest last.
	if err := os.MkdirAll(destinationDir, 0755); err != nil {
		return false, fmt.Sprintf("failed to create '%s': %s", destinationDir, err)
	}
	for _, name := range expectedPublished {
		content, _ := os.ReadFile(filepath.Join(staging, name))
		if err := os.WriteFile(filepath.Join(destinationDir, name), content, 0644); err != nil {
			return false, fmt.Sprintf("failed to write '%s': %s", name, err)
		}
	}
	digestParts := make([]string, 0, len(expectedPublished))
	for _, name := range expectedPublished {
		digestParts = append(digestParts, digests[name])
	}
	manifestDigest := digestText(strings.Join(digestParts, "|"))
	writeJSON("publish-manifest.json", map[string]any{
		"ownedFiles":     expectedPublished,
		"fileDigests":    digests,
		"manifestDigest": manifestDigest,
		"publishedAt":    time.Now().UTC().Format(time.RFC3339),
	})

	return verifyPostcondition(digests)
}

// verifyPostcondition proves blueprint 0004 §6 item 6 ("Executar DocsReader.Read") by
// calling the REAL engine.ReadDocs against destinationDir — not a re-implementation of its
// file-listing/truncation logic — and comparing its output against what publishDocuments
// just wrote. Mirrors SpecificationPublisher.VerifyPostcondition (.NET):
//
//   - names and order: engine.ReadDocs's returned file list must equal publishedFiles
//     exactly — a mismatch (fewer files from a silent mid-run truncation, wrong order, an
//     unexpected extra name) is reported by name.
//   - content, per file: the on-disk file is re-read directly (independent of
//     engine.ReadDocs) and its digest recomputed with the same digestText helper
//     publishDocuments used when staging — a mismatch against expectedDigests means the
//     on-disk file no longer matches what was actually published (tampered, or a
//     partial/corrupted write).
//   - content, via engine.ReadDocs: its own concatenated return value must contain each
//     file's full raw on-disk text as a substring — confirms it picked up the complete,
//     untruncated content of every file. Compared with trailing whitespace trimmed on both
//     sides: engine.ReadDocs itself does a final TrimRight over the WHOLE concatenation (for
//     presentation), which can strip a few trailing newline characters off the last file in
//     alphabetical order — that is not a truncation, so trimming here avoids a false
//     positive while a genuine mid-file truncation (which cuts off real content, not just
//     trailing whitespace) still fails the substring check.
//
// Independently callable (not buried as a private local step reachable only through a full
// publishDocuments call) so it can be exercised directly against a deliberately tampered
// on-disk state.
func verifyPostcondition(expectedDigests map[string]string) (bool, string) {
	content, files := engine.ReadDocs(destinationDir)

	sameFiles := len(files) == len(publishedFiles)
	if sameFiles {
		for i, name := range publishedFiles {
			if files[i] != name {
				sameFiles = false
				break
			}
		}
	}
	if !sameFiles {
		return false, fmt.Sprintf(
			"postcondition failed: DocsReader.Read('%s') returned files [%s], expected [%s].",
			destinationDir, strings.Join(files, ", "), strings.Join(publishedFiles, ", "))
	}

	for _, name := range publishedFiles {
		filePath := filepath.Join(destinationDir, name)
		data, err := os.ReadFile(filePath)
		if err != nil {
			return false, fmt.Sprintf("postcondition failed: could not read '%s': %s", filePath, err)
		}

		text := string(data)
		actualDigest := digestText(text)
		expectedDigest, ok := expectedDigests[name]
		if !ok || actualDigest != expectedDigest {
			return false, fmt.Sprintf(
				"postcondition failed: '%s' on-disk digest '%s' does not match the digest recorded at publish time '%s'.",
				name, actualDigest, expectedDigest)
		}

		if !strings.Contains(content, strings.TrimRight(text, " \t\r\n")) {
			return false, fmt.Sprintf(
				"postcondition failed: DocsReader.Read('%s')'s content does not contain the full on-disk text of '%s' (possible truncation).",
				destinationDir, name)
		}
	}

	return true, ""
}
