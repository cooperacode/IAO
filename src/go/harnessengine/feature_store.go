package harnessengine

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"
)

// The development flow's feature list, persisted to .harness/feature_list.json — the
// "persistent artifact" that survives hard context resets: each session (one feature)
// reads and writes here, without depending on conversation history. All features are born
// with Passes = false; the flow turns one at a time until none remain pending.
//
// Same tolerance as the other stores: absent or unreadable → empty list, never brings the
// run down.
const featureListFilePath = ".harness/feature_list.json"

// DescriptionMaxChars is the ceiling on Feature.Description chars — a defensive quota
// against a verbose driver: the description is reinjected into the `implement` prompt for
// every feature, so without a ceiling it silently inflates every future session's context.
const DescriptionMaxChars = 700

// Feature is one item of the development backlog: priority (lower = higher), whether it
// already passes, which other ids it depends on, a free-form description (up to
// DescriptionMaxChars, reinjected into the `implement` prompt) and explicit reference codes
// from the brief (e.g. "RF-003"; empty when the brief cites none).
type Feature struct {
	Id          int      `json:"id"`
	Title       string   `json:"title"`
	Priority    int      `json:"priority"`
	Passes      bool     `json:"passes"`
	DependsOn   []int    `json:"dependsOn"`
	Description string   `json:"description"`
	References  []string `json:"references"`
}

// rawFeature is the shape the driver returns from `plan` — Id is optional (reindexed by
// order when absent/<=0), and Passes is never read from here: every feature is born pending.
type rawFeature struct {
	Id          int      `json:"id"`
	Title       string   `json:"title"`
	Priority    int      `json:"priority"`
	DependsOn   []int    `json:"dependsOn"`
	Description string   `json:"description"`
	References  []string `json:"references"`
}

type featureList struct {
	Items []Feature `json:"items"`
}

// WriteFeatures overwrites the whole list — used by `plan` (session 0) and MarkFeaturePassed.
func WriteFeatures(features []Feature) {
	if err := ensureDir(stateDir); err != nil {
		fmt.Fprintf(os.Stderr, "[FeatureStore] failed to write: %s\n", err)
		return
	}
	if features == nil {
		features = []Feature{}
	}
	data, err := json.MarshalIndent(featureList{Items: features}, "", "  ")
	if err != nil {
		fmt.Fprintf(os.Stderr, "[FeatureStore] failed to write: %s\n", err)
		return
	}
	if err := writeAtomic(featureListFilePath, string(data)); err != nil {
		fmt.Fprintf(os.Stderr, "[FeatureStore] failed to write: %s\n", err)
	}
}

// ParseFeatures interprets the raw feature array the driver returns from `plan`
// (`[{"id":1,"title":"...","priority":1}, ...]`). Forces Passes = false (every feature is
// born pending) and reindexes missing/duplicate ids by order. Empty list if the JSON
// doesn't parse — the caller re-issues the request (corrective loop), it doesn't bring the
// run down.
func ParseFeatures(rawJSON string) []Feature {
	var parsed []rawFeature
	if err := json.Unmarshal([]byte(rawJSON), &parsed); err != nil {
		fmt.Fprintf(os.Stderr, "[FeatureStore] failed to parse features: %s\n", err)
		return []Feature{}
	}
	if len(parsed) == 0 {
		return []Feature{}
	}

	// Reindex first: dependsOn only makes sense referencing final ids, not the raw
	// (possibly missing/duplicate) ones that came from the driver.
	reindexed := make([]Feature, len(parsed))
	for i, f := range parsed {
		id := f.Id
		if id <= 0 {
			id = i + 1
		}
		dependsOn := f.DependsOn
		if dependsOn == nil {
			dependsOn = []int{}
		}
		references := f.References
		if references == nil {
			references = []string{}
		}
		reindexed[i] = Feature{
			Id:          id,
			Title:       f.Title,
			Priority:    f.Priority,
			Passes:      false,
			DependsOn:   dependsOn,
			Description: truncateDescription(f.Description),
			References:  references,
		}
	}

	if err := dependencyGraphError(reindexed); err != "" {
		fmt.Fprintf(os.Stderr, "[FeatureStore] invalid dependency graph: %s\n", err)
		return []Feature{}
	}

	return reindexed
}

// truncateDescription cuts at DescriptionMaxChars runes — never errors, never rejects the
// whole feature over this, only shortens.
func truncateDescription(description string) string {
	runes := []rune(description)
	if len(runes) > DescriptionMaxChars {
		return string(runes[:DescriptionMaxChars])
	}
	return description
}

// dependencyGraphError returns "" if the DependsOn graph is valid (every id exists, no
// cycle); otherwise a description of the problem. Kahn's algorithm (topological sort): a
// node left outside the resolved set ⇒ cycle. Dangling refs are checked first — otherwise a
// phantom dependency would be counted as eternally unresolved and reported as a "cycle"
// when it's actually an invalid id.
func dependencyGraphError(features []Feature) string {
	validIds := make(map[int]bool, len(features))
	for _, f := range features {
		validIds[f.Id] = true
	}

	var dangling []string
	for _, f := range features {
		for _, dep := range f.DependsOn {
			if !validIds[dep] {
				dangling = append(dangling, fmt.Sprintf("%d->%d", f.Id, dep))
			}
		}
	}
	if len(dangling) > 0 {
		return fmt.Sprintf("dependsOn references nonexistent id(s): %s", strings.Join(dangling, ", "))
	}

	indegree := make(map[int]int, len(features))
	for _, f := range features {
		if _, ok := indegree[f.Id]; !ok {
			indegree[f.Id] = len(f.DependsOn)
		}
	}

	dependents := make(map[int][]int)
	for _, f := range features {
		for _, dep := range f.DependsOn {
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
			if _, ok := indegree[dependent]; ok {
				indegree[dependent]--
				if indegree[dependent] == 0 {
					queue = append(queue, dependent)
				}
			}
		}
	}

	if len(resolved) == len(indegree) {
		return ""
	}

	var cyclic []int
	for id := range indegree {
		if !resolved[id] {
			cyclic = append(cyclic, id)
		}
	}
	sort.Ints(cyclic)
	parts := make([]string, len(cyclic))
	for i, id := range cyclic {
		parts[i] = fmt.Sprintf("%d", id)
	}
	return fmt.Sprintf("cyclic dependency among features: %s", strings.Join(parts, ", "))
}

// LoadFeatures loads the persisted feature list.
func LoadFeatures() []Feature {
	if !fileExists(featureListFilePath) {
		return []Feature{}
	}
	data, err := os.ReadFile(featureListFilePath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[FeatureStore] failed to load: %s\n", err)
		return []Feature{}
	}
	var list featureList
	if err := json.Unmarshal(data, &list); err != nil {
		fmt.Fprintf(os.Stderr, "[FeatureStore] failed to load: %s\n", err)
		return []Feature{}
	}
	if list.Items == nil {
		return []Feature{}
	}
	for i := range list.Items {
		if list.Items[i].DependsOn == nil {
			list.Items[i].DependsOn = []int{}
		}
		if list.Items[i].References == nil {
			list.Items[i].References = []string{}
		}
	}
	return list.Items
}

// NextPendingFeature returns the next feature to implement: the highest priority (lowest
// Priority) among the READY ones (every id in DependsOn already has Passes == true); ties
// broken by Id. nil when there's no ready pending item — this can mean actual completion
// (nothing pending) or blocked dependencies (see Pick in flowsdevelopment).
func NextPendingFeature() *Feature {
	features := LoadFeatures()
	passed := make(map[int]bool)
	for _, f := range features {
		if f.Passes {
			passed[f.Id] = true
		}
	}

	var best *Feature
	for i := range features {
		f := &features[i]
		if f.Passes {
			continue
		}
		ready := true
		for _, dep := range f.DependsOn {
			if !passed[dep] {
				ready = false
				break
			}
		}
		if !ready {
			continue
		}
		if best == nil || f.Priority < best.Priority || (f.Priority == best.Priority && f.Id < best.Id) {
			best = f
		}
	}
	return best
}

// MarkFeaturePassed marks the feature as complete and rewrites the list. No-op if the id
// doesn't exist.
func MarkFeaturePassed(id int) {
	features := LoadFeatures()
	found := false
	for i := range features {
		if features[i].Id == id {
			features[i].Passes = true
			found = true
		}
	}
	if !found {
		return
	}
	WriteFeatures(features)
}

// PendingFeatureCount returns how many features remain (Passes == false).
func PendingFeatureCount() int {
	count := 0
	for _, f := range LoadFeatures() {
		if !f.Passes {
			count++
		}
	}
	return count
}

// AllFeaturesPassing reports whether there are features and all of them passed — the
// loop's termination condition.
func AllFeaturesPassing() bool {
	features := LoadFeatures()
	if len(features) == 0 {
		return false
	}
	for _, f := range features {
		if !f.Passes {
			return false
		}
	}
	return true
}

// ResetFeatures clears the previous run's list — the PRODUCER flow resets it on its `start`.
func ResetFeatures() {
	if !fileExists(featureListFilePath) {
		return
	}
	if err := os.Remove(featureListFilePath); err != nil {
		fmt.Fprintf(os.Stderr, "[FeatureStore] failed to clear: %s\n", err)
	}
}
