package harnessengine

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// LoadGoldenCase loads a single golden-set case from disk.
func LoadGoldenCase(path string) (*GoldenCase, bool) {
	data, err := os.ReadFile(path)
	if err != nil {
		LogError(fmt.Sprintf("[GoldenCaseStore] failed to load %s: %s", path, err))
		return nil, false
	}

	var raw struct {
		Id                 string   `json:"id"`
		Description        string   `json:"description"`
		ExpectedTrajectory []string `json:"expectedTrajectory"`
		RequiredKeys       []string `json:"requiredKeys"`
		ExpectPass         *bool    `json:"expectPass"`
	}
	if err := json.Unmarshal(data, &raw); err != nil {
		LogError(fmt.Sprintf("[GoldenCaseStore] failed to load %s: %s", path, err))
		return nil, false
	}

	// ExpectPass defaults to true when the field is absent from the JSON.
	expectPass := true
	if raw.ExpectPass != nil {
		expectPass = *raw.ExpectPass
	}

	trajectory := raw.ExpectedTrajectory
	if trajectory == nil {
		trajectory = []string{}
	}
	requiredKeys := raw.RequiredKeys
	if requiredKeys == nil {
		requiredKeys = []string{}
	}

	return &GoldenCase{
		Id:                 raw.Id,
		Description:        raw.Description,
		ExpectedTrajectory: trajectory,
		RequiredKeys:       requiredKeys,
		ExpectPass:         expectPass,
	}, true
}

// LoadGoldenCaseDirectory loads every *.json file in directory, sorted by name, skipping
// invalid ones.
func LoadGoldenCaseDirectory(directory string) []GoldenCase {
	if !dirExists(directory) {
		return []GoldenCase{}
	}

	entries, err := os.ReadDir(directory)
	if err != nil {
		return []GoldenCase{}
	}

	var paths []string
	for _, entry := range entries {
		if !entry.IsDir() && strings.HasSuffix(entry.Name(), ".json") {
			paths = append(paths, filepath.Join(directory, entry.Name()))
		}
	}
	sort.Strings(paths)

	cases := []GoldenCase{}
	for _, path := range paths {
		if c, ok := LoadGoldenCase(path); ok {
			cases = append(cases, *c)
		}
	}
	return cases
}
