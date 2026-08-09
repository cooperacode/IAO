package harnessengine

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// Persists proposed and accepted global plan revisions for audit and resume.
const (
	// ReplanProposalPath is where the driver writes its proposed revision — read by
	// ReadPlanRevisionProposal, the same way plan.json is written for `plan`.
	ReplanProposalPath = ".harness/replan.json"

	planRevisionsDir        = ".harness/plans"
	currentPlanRevisionPath = ".harness/plan_revision.json"
)

// AppliedPlanRevision is the audit record of one accepted plan revision.
type AppliedPlanRevision struct {
	Version                int                    `json:"version"`
	AppliedAt              time.Time              `json:"appliedAt"`
	Reason                 string                 `json:"reason"`
	AlternativesConsidered []string               `json:"alternativesConsidered"`
	BasedOnObservationIds  []string               `json:"basedOnObservationIds"`
	PlanFingerprint        string                 `json:"planFingerprint"`
	Evaluation             PlanRevisionEvaluation `json:"evaluation"`
	Features               []Feature              `json:"features"`
}

// ReadPlanRevisionProposal reads the driver-written proposal at ReplanProposalPath. nil if
// absent/unreadable/unparseable.
func ReadPlanRevisionProposal() *PlanRevision {
	if !fileExists(ReplanProposalPath) {
		return nil
	}
	data, err := os.ReadFile(ReplanProposalPath)
	if err != nil {
		LogError(fmt.Sprintf("[PlanRevisionStore] failed to read proposal: %s", err))
		return nil
	}
	var revision PlanRevision
	if err := json.Unmarshal(data, &revision); err != nil {
		LogError(fmt.Sprintf("[PlanRevisionStore] failed to read proposal: %s", err))
		return nil
	}
	return &revision
}

// PlanRevisionCount returns the Version of the currently applied revision, 0 if none.
func PlanRevisionCount() int {
	if !fileExists(currentPlanRevisionPath) {
		return 0
	}
	data, err := os.ReadFile(currentPlanRevisionPath)
	if err != nil {
		return 0
	}
	var applied AppliedPlanRevision
	if err := json.Unmarshal(data, &applied); err != nil {
		return 0
	}
	return applied.Version
}

// RecordPlanRevision persists an accepted revision as both the current pointer
// (.harness/plan_revision.json) and a numbered history entry (.harness/plans/plan-v{N}.json).
func RecordPlanRevision(revision PlanRevision, features []Feature, evaluation PlanRevisionEvaluation) {
	persisted := make([]Feature, len(features))
	copy(persisted, features)

	applied := AppliedPlanRevision{
		Version:                PlanRevisionCount() + 1,
		AppliedAt:              time.Now().UTC(),
		Reason:                 revision.Reason,
		AlternativesConsidered: revision.Alternatives,
		BasedOnObservationIds:  revision.ObservationIds,
		PlanFingerprint:        PlanRevisionFingerprint(features),
		Evaluation:             evaluation,
		Features:               persisted,
	}

	if err := ensureDir(planRevisionsDir); err != nil {
		LogError(fmt.Sprintf("[PlanRevisionStore] failed to record: %s", err))
		return
	}
	data, err := json.MarshalIndent(applied, "", "  ")
	if err != nil {
		LogError(fmt.Sprintf("[PlanRevisionStore] failed to record: %s", err))
		return
	}
	if err := writeAtomic(currentPlanRevisionPath, string(data)); err != nil {
		LogError(fmt.Sprintf("[PlanRevisionStore] failed to record: %s", err))
		return
	}
	historyPath := filepath.Join(planRevisionsDir, fmt.Sprintf("plan-v%d.json", applied.Version))
	if err := writeAtomic(historyPath, string(data)); err != nil {
		LogError(fmt.Sprintf("[PlanRevisionStore] failed to record: %s", err))
	}
}

// HasPlanFingerprint reports whether the given fingerprint already appears in the revision
// history — dedupe guard against reapplying the identical plan in the same run.
func HasPlanFingerprint(fingerprint string) bool {
	if !dirExists(planRevisionsDir) {
		return false
	}
	entries, err := os.ReadDir(planRevisionsDir)
	if err != nil {
		return false
	}
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() || !strings.HasPrefix(name, "plan-v") || !strings.HasSuffix(name, ".json") {
			continue
		}
		data, err := os.ReadFile(filepath.Join(planRevisionsDir, name))
		if err != nil {
			continue // corrupt history never approves a proposal
		}
		var applied AppliedPlanRevision
		if err := json.Unmarshal(data, &applied); err != nil {
			continue
		}
		if applied.PlanFingerprint == fingerprint {
			return true
		}
	}
	return false
}

// PlanRevisionFingerprint canonicalizes features (sorted by id, Passes forced false so a
// fingerprint doesn't change just because features later pass) and returns the lowercase hex
// SHA-256 of their JSON encoding.
func PlanRevisionFingerprint(features []Feature) string {
	canonical := make([]Feature, len(features))
	copy(canonical, features)
	sort.Slice(canonical, func(i, j int) bool { return canonical[i].Id < canonical[j].Id })
	for i := range canonical {
		canonical[i].Passes = false
	}

	data, err := json.Marshal(featureList{Items: canonical})
	if err != nil {
		LogError(fmt.Sprintf("[PlanRevisionStore] failed to fingerprint: %s", err))
		return ""
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:])
}

// ResetPlanRevisions clears the proposal, current pointer and history — paired with
// ResetFeatures/ResetPlanObservations.
func ResetPlanRevisions() {
	if fileExists(ReplanProposalPath) {
		if err := os.Remove(ReplanProposalPath); err != nil {
			LogError(fmt.Sprintf("[PlanRevisionStore] failed to reset: %s", err))
		}
	}
	if fileExists(currentPlanRevisionPath) {
		if err := os.Remove(currentPlanRevisionPath); err != nil {
			LogError(fmt.Sprintf("[PlanRevisionStore] failed to reset: %s", err))
		}
	}
	if dirExists(planRevisionsDir) {
		if err := os.RemoveAll(planRevisionsDir); err != nil {
			LogError(fmt.Sprintf("[PlanRevisionStore] failed to reset: %s", err))
		}
	}
}
