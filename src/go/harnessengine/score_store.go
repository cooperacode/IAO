package harnessengine

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
)

// Persists the result of each evaluation to .harness/scores.jsonl (one line per run). It's
// the "grades" side of Telemetry, consumed by reports.
const scoreFilePath = ".harness/scores.jsonl"

// ScoreReport is the grade of one evaluation: the deterministic gate's verdict (0 tokens)
// and, when it passes, the LLM judge's score. JudgeScore = 0 when the gate fails.
type ScoreReport struct {
	Timestamp      string `json:"timestamp"`
	GatePassed     bool   `json:"gatePassed"`
	GateDetail     string `json:"gateDetail"`
	JudgeScore     int    `json:"judgeScore"`
	JudgeRationale string `json:"judgeRationale"`
}

// AppendScore appends a score report.
func AppendScore(report ScoreReport) {
	if err := ensureDir(stateDir); err != nil {
		fmt.Fprintf(os.Stderr, "[ScoreStore] failed to write: %s\n", err)
		return
	}
	line, err := json.Marshal(report)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[ScoreStore] failed to write: %s\n", err)
		return
	}
	f, err := os.OpenFile(scoreFilePath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[ScoreStore] failed to write: %s\n", err)
		return
	}
	defer f.Close()
	if _, err := f.WriteString(string(line) + "\n"); err != nil {
		fmt.Fprintf(os.Stderr, "[ScoreStore] failed to write: %s\n", err)
	}
}

// LoadScores loads every persisted score report.
func LoadScores() []ScoreReport {
	if !fileExists(scoreFilePath) {
		return []ScoreReport{}
	}
	data, err := os.ReadFile(scoreFilePath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "[ScoreStore] failed to load: %s\n", err)
		return []ScoreReport{}
	}
	reports := []ScoreReport{}
	for _, line := range strings.Split(string(data), "\n") {
		if strings.TrimSpace(line) == "" {
			continue
		}
		var report ScoreReport
		if err := json.Unmarshal([]byte(line), &report); err == nil {
			reports = append(reports, report)
		}
	}
	return reports
}
