package harnessengine

import (
	"encoding/json"
	"os"
	"strconv"
	"strings"
)

// ContextUsage is optional telemetry supplied by the driver. The engine never
// derives it from driver-specific rollout files.
type ContextUsage struct {
	Schema              string `json:"schema,omitempty"`
	SessionID           string `json:"sessionId,omitempty"`
	ContextWindowTokens int    `json:"contextWindowTokens"`
	ContextUsedTokens   int    `json:"contextUsedTokens"`
	Source              string `json:"source,omitempty"`
}

func ContextUsageFromEnvironment() *ContextUsage {
	raw := strings.TrimSpace(os.Getenv("HARNESS_CONTEXT_USAGE_JSON"))
	if raw == "" {
		return nil
	}
	var usage ContextUsage
	if err := json.Unmarshal([]byte(raw), &usage); err != nil {
		return nil
	}
	return &usage
}

const (
	contextBoundaryKey  = "context_boundary_seen"
	contextFeaturesKey  = "context_features"
	contextRatioKey     = "context_ratio"
	contextUsageSeenKey = "context_usage_seen"
)

func ObserveContextUsage(usage *ContextUsage) {
	if usage == nil || usage.ContextWindowTokens <= 0 || usage.ContextUsedTokens < 0 {
		return
	}
	ratio := float64(usage.ContextUsedTokens) / float64(usage.ContextWindowTokens)
	if ratio < 0 {
		ratio = 0
	}
	if ratio > 1 {
		ratio = 1
	}
	SetState(contextRatioKey, strconv.FormatFloat(ratio, 'f', 6, 64))
	SetState(contextUsageSeenKey, "true")
}

// NewFeaturePrefix emits the marker only when the reset policy requests a new
// driver context. Retries and verification prompts do not call this function.
func NewFeaturePrefix() string {
	reset := shouldResetContext()
	SetState(contextBoundaryKey, "true")
	if reset {
		SetState(contextFeaturesKey, "1")
		SetState(contextRatioKey, "0")
		SetState(contextUsageSeenKey, "false")
		return "=== NEW SESSION (clean context) ===\n\n"
	}

	features := readContextInt(contextFeaturesKey, 0) + 1
	SetState(contextFeaturesKey, strconv.Itoa(features))
	return ""
}

func shouldResetContext() bool {
	config := CurrentConfig()
	mode := strings.ToLower(strings.TrimSpace(config.ContextResetMode))
	if mode == "never" {
		return false
	}
	if mode == "per-feature" {
		return true
	}
	if GetState(contextBoundaryKey) == nil {
		return true
	}
	if ratioValue := GetState(contextRatioKey); ratioValue != nil {
		if ratio, err := strconv.ParseFloat(*ratioValue, 64); err == nil && ratio >= config.ContextResetThreshold {
			return true
		}
	}
	usageSeen := GetState(contextUsageSeenKey)
	return (usageSeen == nil || *usageSeen != "true") &&
		readContextInt(contextFeaturesKey, 0) >= config.ContextFallbackFeatures
}

func readContextInt(key string, fallback int) int {
	value := GetState(key)
	if value == nil {
		return fallback
	}
	parsed, err := strconv.Atoi(*value)
	if err != nil || parsed < 0 {
		return fallback
	}
	return parsed
}
