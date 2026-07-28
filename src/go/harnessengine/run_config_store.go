package harnessengine

import (
	"encoding/json"
	"fmt"
	"os"
)

// Persists verifyCmd/targetDir (captured once by `plan`) in .harness/run_config.json —
// deliberately outside of state.json. TaskRegistry resets state.json unconditionally on
// every `start`, before any domain code runs; a resumed run (see
// flowsdevelopment.DevelopmentTasks.Start) still needs these two values for smoke/verify to
// work, so they must survive that reset.
const runConfigFilePath = ".harness/run_config.json"

// RunConfig holds the verification command, target directory, and run identity (RFC §6.4),
// all captured once by `plan`. RunId is generated only on a genuinely new run — the same
// moment RunConfigStore.Write is called after ResetRunConfig — and survives every resume
// because this file is untouched when `start` decides there's pending work.
type RunConfig struct {
	VerifyCmd string `json:"verifyCmd"`
	TargetDir string `json:"targetDir"`
	RunId     string `json:"runId"`
}

// DefaultRunConfig returns the zero-value run config (target dir ".").
func DefaultRunConfig() RunConfig {
	return RunConfig{TargetDir: "."}
}

// WriteRunConfig persists the run configuration — same lifecycle as feature_list.json
// (written by `plan`, cleared only when `start` decides there's no run to resume).
func WriteRunConfig(config RunConfig) {
	if err := ensureDir(stateDir); err != nil {
		fmt.Fprintf(os.Stderr, "[RunConfigStore] falha ao gravar: %s\n", err)
		return
	}
	data, err := json.Marshal(config)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[RunConfigStore] falha ao gravar: %s\n", err)
		return
	}
	if err := writeAtomic(runConfigFilePath, string(data)); err != nil {
		fmt.Fprintf(os.Stderr, "[RunConfigStore] falha ao gravar: %s\n", err)
	}
}

// LoadRunConfig reads the persisted configuration, or the defaults if nothing was written yet.
func LoadRunConfig() RunConfig {
	if fileExists(runConfigFilePath) {
		data, err := os.ReadFile(runConfigFilePath)
		if err == nil {
			config := DefaultRunConfig()
			if err := json.Unmarshal(data, &config); err == nil {
				if config.TargetDir == "" {
					config.TargetDir = "."
				}
				return config
			}
			fmt.Fprintf(os.Stderr, "[RunConfigStore] falha ao carregar: %s\n", err)
		} else {
			fmt.Fprintf(os.Stderr, "[RunConfigStore] falha ao carregar: %s\n", err)
		}
	}
	return DefaultRunConfig()
}

// ResetRunConfig clears the file on a genuinely new run — paired with ResetFeatures.
func ResetRunConfig() {
	if !fileExists(runConfigFilePath) {
		return
	}
	if err := os.Remove(runConfigFilePath); err != nil {
		fmt.Fprintf(os.Stderr, "[RunConfigStore] falha ao limpar: %s\n", err)
	}
}
