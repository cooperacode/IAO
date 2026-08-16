package harnessengine

import (
	"fmt"
	"sort"
	"strings"
)

// PlanRevisionVerdict is the deterministic gate's outcome for a proposed plan revision.
type PlanRevisionVerdict string

const (
	PlanRevisionApprove             PlanRevisionVerdict = "approve"
	PlanRevisionApproveWithWarnings PlanRevisionVerdict = "approve_with_warnings"
	PlanRevisionReject              PlanRevisionVerdict = "reject"
)

// PlanRevisionIssue is one error or warning surfaced by the evaluator.
type PlanRevisionIssue struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

// PlanDiff summarizes how the proposed plan differs from the current one.
type PlanDiff struct {
	Added                     []int    `json:"added"`
	Removed                   []int    `json:"removed"`
	Modified                  []int    `json:"modified"`
	Reprioritized             []int    `json:"reprioritized"`
	RemovedReferences         []string `json:"removedReferences"`
	RemovedAcceptanceCriteria []string `json:"removedAcceptanceCriteria"`
}

// HasChanges reports whether the diff carries any structural change.
func (d PlanDiff) HasChanges() bool {
	return len(d.Added)+len(d.Removed)+len(d.Modified)+len(d.Reprioritized) > 0
}

// PlanRevisionEvaluation is the full result of evaluating a PlanRevision.
type PlanRevisionEvaluation struct {
	Verdict  PlanRevisionVerdict `json:"verdict"`
	Errors   []PlanRevisionIssue `json:"errors"`
	Warnings []PlanRevisionIssue `json:"warnings"`
	Diff     PlanDiff            `json:"diff"`
}

// Passed reports whether the evaluation is anything other than an outright rejection
// (Approve or ApproveWithWarnings).
func (e PlanRevisionEvaluation) Passed() bool {
	return e.Verdict != PlanRevisionReject
}

// EvaluatePlanRevision is the pure, deterministic gate for global plan revisions. It judges
// evidence and invariants only; it does not interpret whether a technical strategy is
// semantically good.
func EvaluatePlanRevision(
	current []Feature, revision PlanRevision, observations []PlanObservation,
	maxFeatures, remainingSteps, stepsPerFeature int,
) PlanRevisionEvaluation {
	errors := []PlanRevisionIssue{}
	warnings := []PlanRevisionIssue{}
	addError := func(code, message string) {
		errors = append(errors, PlanRevisionIssue{Code: code, Message: message})
	}

	proposed := revision.Features
	currentById := make(map[int]Feature, len(current))
	for _, f := range current {
		currentById[f.Id] = f
	}
	proposedById := make(map[int]Feature, len(proposed))
	for _, f := range proposed {
		if _, exists := proposedById[f.Id]; !exists {
			proposedById[f.Id] = f
		}
	}
	diff := buildPlanDiff(current, proposed)

	if strings.TrimSpace(revision.Reason) == "" {
		addError("REVISION_REASON_REQUIRED", "A revision reason is required.")
	}

	alternatives := distinctNormalizedStrings(revision.Alternatives)
	if len(alternatives) < 2 {
		addError("ALTERNATIVES_REQUIRED", "At least two distinct alternatives are required.")
	}

	if len(proposed) == 0 {
		addError("PLAN_EMPTY", "The revised plan must contain features.")
	}
	if len(proposed) > maxFeatures {
		addError("FEATURE_LIMIT", fmt.Sprintf("The revised plan exceeds the %d-feature limit.", maxFeatures))
	}
	for _, f := range proposed {
		if f.Id <= 0 || strings.TrimSpace(f.Title) == "" || f.Priority <= 0 {
			addError("FEATURE_INVALID", "Every feature needs a positive unique id, title and positive priority.")
			break
		}
	}
	{
		seen := make(map[int]bool, len(proposed))
		for _, f := range proposed {
			if seen[f.Id] {
				addError("FEATURE_ID_DUPLICATE", "Feature ids must be unique.")
				break
			}
			seen[f.Id] = true
		}
	}

	for _, passed := range current {
		if !passed.Passes {
			continue
		}
		retained, ok := proposedById[passed.Id]
		if !ok {
			addError("PASSED_FEATURE_REMOVED", fmt.Sprintf("Passed feature #%d cannot be removed.", passed.Id))
		} else if !evaluatorSameDefinition(passed, retained) {
			addError("PASSED_FEATURE_MODIFIED", fmt.Sprintf("Passed feature #%d cannot be modified.", passed.Id))
		}
	}

	passedCurrentIds := make(map[int]bool, len(current))
	for _, f := range current {
		if f.Passes {
			passedCurrentIds[f.Id] = true
		}
	}
	validatePlanGraph(proposed, passedCurrentIds, addError)

	for _, reference := range diff.RemovedReferences {
		addError("REQUIREMENT_COVERAGE_REMOVED", fmt.Sprintf("Brief reference '%s' is no longer covered.", reference))
	}
	for _, acceptance := range diff.RemovedAcceptanceCriteria {
		addError("ACCEPTANCE_REMOVED", fmt.Sprintf("Acceptance criterion '%s' is no longer covered.", acceptance))
	}

	knownObservationIds := make(map[string]bool, len(observations))
	for _, o := range observations {
		knownObservationIds[o.Id] = true
	}
	if len(revision.ObservationIds) == 0 {
		addError("OBSERVATION_REQUIRED", "The revision must cite at least one persisted observation.")
	}
	seenObservationIds := make(map[string]bool, len(revision.ObservationIds))
	for _, id := range revision.ObservationIds {
		if seenObservationIds[id] {
			continue
		}
		seenObservationIds[id] = true
		if !knownObservationIds[id] {
			addError("OBSERVATION_UNKNOWN", fmt.Sprintf("Observation '%s' does not exist in the run evidence.", id))
		}
	}

	if !diff.HasChanges() {
		addError("PLAN_UNCHANGED", "The revision does not change the current plan.")
	}
	fingerprint := PlanRevisionFingerprint(proposed)
	if HasPlanFingerprint(fingerprint) {
		addError("PLAN_REPEATED", "The same revised plan was already applied in this run.")
	}

	pending := 0
	for _, f := range proposed {
		old, existsOld := currentById[f.Id]
		if !existsOld || !old.Passes {
			pending++
		}
	}
	worstCaseSteps := pending * stepsPerFeature
	if remainingSteps >= 0 && worstCaseSteps > remainingSteps {
		warnings = append(warnings, PlanRevisionIssue{
			Code:    "BUDGET_RISK",
			Message: fmt.Sprintf("The revised plan may require %d steps with %d remaining.", worstCaseSteps, remainingSteps),
		})
	}

	verdict := PlanRevisionApprove
	switch {
	case len(errors) > 0:
		verdict = PlanRevisionReject
	case len(warnings) > 0:
		verdict = PlanRevisionApproveWithWarnings
	}

	return PlanRevisionEvaluation{Verdict: verdict, Errors: errors, Warnings: warnings, Diff: diff}
}

func buildPlanDiff(current, proposed []Feature) PlanDiff {
	before := make(map[int]Feature, len(current))
	for _, f := range current {
		before[f.Id] = f
	}
	after := make(map[int]Feature, len(proposed))
	for _, f := range proposed {
		if _, exists := after[f.Id]; !exists {
			after[f.Id] = f
		}
	}

	added := []int{}
	for id := range after {
		if _, ok := before[id]; !ok {
			added = append(added, id)
		}
	}
	removed := []int{}
	for id := range before {
		if _, ok := after[id]; !ok {
			removed = append(removed, id)
		}
	}
	reprioritized := []int{}
	modified := []int{}
	for id, b := range before {
		a, ok := after[id]
		if !ok {
			continue
		}
		if b.Priority != a.Priority {
			reprioritized = append(reprioritized, id)
		}
		if !evaluatorSameDefinitionExceptPriority(b, a) {
			modified = append(modified, id)
		}
	}
	sort.Ints(added)
	sort.Ints(removed)
	sort.Ints(reprioritized)
	sort.Ints(modified)

	oldRefOrder, _ := collectNormalizedSet(current, func(f Feature) []string { return f.References }, nil)
	_, newRefLookup := collectNormalizedSet(proposed, func(f Feature) []string { return f.References }, nil)
	removedReferences := setDifferenceSorted(oldRefOrder, newRefLookup)

	oldAcceptanceOrder, _ := collectNormalizedSet(current, func(f Feature) []string { return f.ImplementationContext.Acceptance }, normalizeWhitespace)
	_, newAcceptanceLookup := collectNormalizedSet(proposed, func(f Feature) []string { return f.ImplementationContext.Acceptance }, normalizeWhitespace)
	removedAcceptance := setDifferenceSorted(oldAcceptanceOrder, newAcceptanceLookup)

	return PlanDiff{
		Added: added, Removed: removed, Modified: modified, Reprioritized: reprioritized,
		RemovedReferences: removedReferences, RemovedAcceptanceCriteria: removedAcceptance,
	}
}

// validatePlanGraph checks the proposed dependency graph: no self-dependency, no dangling
// reference, no cycle (Kahn's algorithm), and at least one executable (ready) feature among
// the pending ones. Mirrors dependencyGraphError's algorithm but reports every problem
// through addError instead of stopping at the first one, and additionally requires the
// pending subgraph to have a ready feature.
func validatePlanGraph(features []Feature, passedIds map[int]bool, addError func(code, message string)) {
	seen := make(map[int]bool, len(features))
	for _, f := range features {
		if seen[f.Id] {
			return
		}
		seen[f.Id] = true
	}

	ids := make(map[int]bool, len(features))
	for _, f := range features {
		ids[f.Id] = true
	}

	missingFound := false
	for _, f := range features {
		if containsInt(f.DependsOn, f.Id) {
			addError("SELF_DEPENDENCY", fmt.Sprintf("Feature #%d depends on itself.", f.Id))
		}
		for _, dep := range f.DependsOn {
			if !ids[dep] {
				addError("DEPENDENCY_MISSING", fmt.Sprintf("Feature #%d depends on missing feature #%d.", f.Id, dep))
				missingFound = true
			}
		}
	}
	if missingFound {
		return
	}

	indegree := make(map[int]int, len(features))
	dependents := make(map[int][]int, len(features))
	for _, f := range features {
		deps := uniqueInts(f.DependsOn)
		indegree[f.Id] = len(deps)
		for _, dep := range deps {
			dependents[dep] = append(dependents[dep], f.Id)
		}
	}

	var queue []int
	for id, d := range indegree {
		if d == 0 {
			queue = append(queue, id)
		}
	}
	resolved := make(map[int]bool, len(indegree))
	for len(queue) > 0 {
		id := queue[0]
		queue = queue[1:]
		if resolved[id] {
			continue
		}
		resolved[id] = true
		for _, dependent := range dependents[id] {
			indegree[dependent]--
			if indegree[dependent] == 0 {
				queue = append(queue, dependent)
			}
		}
	}
	if len(resolved) != len(features) {
		addError("DEPENDENCY_CYCLE", "The revised dependency graph contains a cycle.")
	}

	var pending []Feature
	for _, f := range features {
		if !passedIds[f.Id] {
			pending = append(pending, f)
		}
	}
	if len(pending) > 0 {
		anyReady := false
		for _, f := range pending {
			ready := true
			for _, dep := range f.DependsOn {
				if !passedIds[dep] {
					ready = false
					break
				}
			}
			if ready {
				anyReady = true
				break
			}
		}
		if !anyReady {
			addError("PLAN_NO_READY_FEATURE", "The revised plan has pending work but no executable feature.")
		}
	}
}

func evaluatorSameDefinition(left, right Feature) bool {
	return left.Priority == right.Priority && evaluatorSameDefinitionExceptPriority(left, right)
}

func evaluatorSameDefinitionExceptPriority(left, right Feature) bool {
	return left.Id == right.Id &&
		left.Title == right.Title &&
		left.Description == right.Description &&
		intsEqualSeq(left.DependsOn, right.DependsOn) &&
		stringsEqualSeq(left.References, right.References) &&
		stringsEqualSeq(left.ImplementationContext.Requirements, right.ImplementationContext.Requirements) &&
		stringsEqualSeq(left.ImplementationContext.Decisions, right.ImplementationContext.Decisions) &&
		stringsEqualSeq(left.ImplementationContext.Constraints, right.ImplementationContext.Constraints) &&
		stringsEqualSeq(left.ImplementationContext.Files, right.ImplementationContext.Files) &&
		stringsEqualSeq(left.ImplementationContext.Acceptance, right.ImplementationContext.Acceptance)
}

func containsInt(values []int, target int) bool {
	for _, v := range values {
		if v == target {
			return true
		}
	}
	return false
}

func intsEqualSeq(a, b []int) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func stringsEqualSeq(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

// normalizeWhitespace collapses runs of whitespace to a single space, mirroring
// string.Join(value.Split(null, RemoveEmptyEntries), " ") on the .NET side.
func normalizeWhitespace(value string) string {
	return strings.Join(strings.Fields(value), " ")
}

// distinctNormalizedStrings filters blanks, whitespace-normalizes, and case-sensitively
// dedupes, preserving first-seen order.
func distinctNormalizedStrings(values []string) []string {
	seen := make(map[string]bool, len(values))
	result := make([]string, 0, len(values))
	for _, v := range values {
		if strings.TrimSpace(v) == "" {
			continue
		}
		normalized := normalizeWhitespace(v)
		if seen[normalized] {
			continue
		}
		seen[normalized] = true
		result = append(result, normalized)
	}
	return result
}

// collectNormalizedSet gathers non-blank values from every feature (via extract), optionally
// normalizing each (e.g. whitespace collapse), and case-insensitively dedupes. Returns the
// first-seen values in original (normalized) casing (order) plus a lowercased lookup set —
// mirrors a .NET HashSet<string>(StringComparer.OrdinalIgnoreCase) built the same way.
func collectNormalizedSet(features []Feature, extract func(Feature) []string, normalize func(string) string) ([]string, map[string]bool) {
	lookup := map[string]bool{}
	order := []string{}
	for _, f := range features {
		for _, raw := range extract(f) {
			if strings.TrimSpace(raw) == "" {
				continue
			}
			value := raw
			if normalize != nil {
				value = normalize(raw)
			}
			key := strings.ToLower(value)
			if lookup[key] {
				continue
			}
			lookup[key] = true
			order = append(order, value)
		}
	}
	return order, lookup
}

// setDifferenceSorted returns the values from order whose lowercased key is absent from
// otherLookup, sorted ordinally (ascending) — mirrors oldSet.Except(newSet).Order().
func setDifferenceSorted(order []string, otherLookup map[string]bool) []string {
	diff := []string{}
	for _, v := range order {
		if !otherLookup[strings.ToLower(v)] {
			diff = append(diff, v)
		}
	}
	sort.Strings(diff)
	return diff
}
