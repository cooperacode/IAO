package harnessengine

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"time"
)

// Append-only evidence that may justify a global plan revision — persisted to
// .harness/plan_observations.jsonl (JSON Lines: one observation per line, never rewritten).
const planObservationsFilePath = ".harness/plan_observations.jsonl"

// PlanObservation is one piece of evidence (e.g. a feature failing deterministic
// verification repeatedly) recorded for later citation by a PlanRevision.
type PlanObservation struct {
	Id         string    `json:"id"`
	Kind       string    `json:"kind"`
	FeatureId  *int      `json:"featureId"`
	Summary    string    `json:"summary"`
	Evidence   []string  `json:"evidence"`
	ObservedAt time.Time `json:"observedAt"`
}

// AppendPlanObservation records a new observation and returns it. Id is
// "OBS-{running count + 1, zero-padded to 3 digits}" (e.g. "OBS-001").
func AppendPlanObservation(kind string, featureId *int, summary string, evidence ...string) PlanObservation {
	filtered := make([]string, 0, len(evidence))
	for _, e := range evidence {
		if strings.TrimSpace(e) != "" {
			filtered = append(filtered, e)
		}
	}

	observation := PlanObservation{
		Id:         fmt.Sprintf("OBS-%03d", len(LoadPlanObservations())+1),
		Kind:       kind,
		FeatureId:  featureId,
		Summary:    summary,
		Evidence:   filtered,
		ObservedAt: time.Now().UTC(),
	}

	if err := ensureDir(stateDir); err != nil {
		LogError(fmt.Sprintf("[PlanObservationStore] failed to append: %s", err))
		return observation
	}
	data, err := json.Marshal(observation)
	if err != nil {
		LogError(fmt.Sprintf("[PlanObservationStore] failed to append: %s", err))
		return observation
	}
	f, err := os.OpenFile(planObservationsFilePath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		LogError(fmt.Sprintf("[PlanObservationStore] failed to append: %s", err))
		return observation
	}
	defer f.Close()
	if _, err := f.WriteString(string(data) + "\n"); err != nil {
		LogError(fmt.Sprintf("[PlanObservationStore] failed to append: %s", err))
	}
	return observation
}

// LoadPlanObservations reads every persisted observation, in append order. Tolerates a
// partial final line (e.g. after an interruption mid-write) by skipping it.
func LoadPlanObservations() []PlanObservation {
	if !fileExists(planObservationsFilePath) {
		return []PlanObservation{}
	}
	data, err := os.ReadFile(planObservationsFilePath)
	if err != nil {
		LogError(fmt.Sprintf("[PlanObservationStore] failed to load: %s", err))
		return []PlanObservation{}
	}

	result := []PlanObservation{}
	for _, line := range strings.Split(string(data), "\n") {
		if strings.TrimSpace(line) == "" {
			continue
		}
		var observation PlanObservation
		if err := json.Unmarshal([]byte(line), &observation); err == nil {
			result = append(result, observation)
		}
	}
	return result
}

// ResetPlanObservations clears the evidence log — paired with ResetFeatures/ResetPlanRevisions.
func ResetPlanObservations() {
	if !fileExists(planObservationsFilePath) {
		return
	}
	if err := os.Remove(planObservationsFilePath); err != nil {
		LogError(fmt.Sprintf("[PlanObservationStore] failed to reset: %s", err))
	}
}
