package harnessengine

import (
	"encoding/json"
	"fmt"
	"os"
	"strconv"
	"strings"
	"sync"
)

// HarnessConfig holds the harness's fixed variables, externalized into a harness.json at
// the repo root. Centralizing them here lets each flow/environment tune the ceilings
// without recompiling. Absent or unreadable → falls back to DefaultHarnessConfig (same
// tolerance as the other stores: config is optional input, it must never bring the run down).
type HarnessConfig struct {
	MaxSteps            int    `json:"maxSteps"`
	MaxInstructionChars int    `json:"maxInstructionChars"`
	DocsMaxChars        int    `json:"docsMaxChars"`
	DocsFolder          string `json:"docsFolder"`
	TimeoutMs           int    `json:"timeoutMs"`
}

const harnessConfigPath = "harness.json"

// maxAllowedTimeoutMs is the hard ceiling on TimeoutMs, regardless of source (harness.json
// OR the env var below). harness.json lives in the working directory the supervised agent
// itself controls: without this ceiling, the agent could edit the file to grant itself an
// arbitrarily high timeout and never be cut by the time guard (see TaskRegistry).
const maxAllowedTimeoutMs = 5 * 60_000

// timeoutMsEnvVar, when set, overrides harness.json's timeoutMs. Unlike the file, the env
// var is set by the parent process invoking each harness step — outside the working
// directory the supervised agent controls — so it cannot be self-edited by the same agent
// the timeout is meant to contain.
const timeoutMsEnvVar = "HARNESS_TIMEOUT_MS"

// DefaultHarnessConfig returns the built-in defaults. MaxInstructionChars = 0 disables the
// cost ceiling (only the step ceiling applies). TimeoutMs = 0 disables the per-step time
// guard (same convention as cost). The enabled value ships in harness.json, NOT here: if
// the default were > 0, a harness.json that omits the field (deserializes to 0) could never
// mean "disabled".
func DefaultHarnessConfig() HarnessConfig {
	return HarnessConfig{
		MaxSteps:            12,
		MaxInstructionChars: 0,
		DocsMaxChars:        40_000,
		DocsFolder:          "docs",
		TimeoutMs:           0,
	}
}

var (
	configMu     sync.Mutex
	cachedConfig *HarnessConfig
)

// CurrentConfig is loaded once per process (each harness invocation is a fresh process, so
// "once" means "once per loop turn"). Static readers — DocsReader, RefinementTasks —
// consume from here without needing the config passed as a parameter.
func CurrentConfig() HarnessConfig {
	configMu.Lock()
	defer configMu.Unlock()
	if cachedConfig == nil {
		loaded := LoadConfig()
		cachedConfig = &loaded
	}
	return *cachedConfig
}

// ReloadConfig forces a re-read of harness.json — for tests and long-lived drivers.
func ReloadConfig() HarnessConfig {
	configMu.Lock()
	defer configMu.Unlock()
	loaded := LoadConfig()
	cachedConfig = &loaded
	return loaded
}

// LoadConfig re-reads harness.json from disk; any failure returns DefaultHarnessConfig.
func LoadConfig() HarnessConfig {
	config := DefaultHarnessConfig()

	path := ResolvePath(harnessConfigPath)
	if fileExists(path) {
		data, err := os.ReadFile(path)
		if err == nil {
			var parsed HarnessConfig
			if err := json.Unmarshal(data, &parsed); err == nil {
				config = parsed
			} else {
				fmt.Fprintf(os.Stderr, "[HarnessConfig] falha ao carregar; usando defaults: %s\n", err)
				config = DefaultHarnessConfig()
			}
		} else {
			fmt.Fprintf(os.Stderr, "[HarnessConfig] falha ao carregar; usando defaults: %s\n", err)
		}
	}

	return normalizeConfig(applyTimeoutEnvOverride(config))
}

// applyTimeoutEnvOverride: see timeoutMsEnvVar. Absent/invalid is silently ignored — same
// tolerance as the rest of the config: it's optional input, it can't bring the run down.
func applyTimeoutEnvOverride(config HarnessConfig) HarnessConfig {
	raw, ok := os.LookupEnv(timeoutMsEnvVar)
	if !ok {
		return config
	}
	timeoutMs, err := strconv.Atoi(strings.TrimSpace(raw))
	if err != nil {
		return config
	}
	config.TimeoutMs = timeoutMs
	return config
}

// normalizeConfig: a partial harness.json deserializes missing fields as 0/"". Zero is only
// valid where it means "disabled" (cost ceilings); elsewhere, a missing field means default.
func normalizeConfig(config HarnessConfig) HarnessConfig {
	def := DefaultHarnessConfig()

	if config.MaxSteps <= 0 {
		config.MaxSteps = def.MaxSteps
	}
	if config.MaxInstructionChars < 0 {
		config.MaxInstructionChars = 0
	}
	if config.DocsMaxChars <= 0 {
		config.DocsMaxChars = def.DocsMaxChars
	}
	if strings.TrimSpace(config.DocsFolder) == "" {
		config.DocsFolder = def.DocsFolder
	}
	config.TimeoutMs = clamp(config.TimeoutMs, 0, maxAllowedTimeoutMs)

	return config
}

func clamp(value, min, max int) int {
	if value < min {
		return min
	}
	if value > max {
		return max
	}
	return value
}
