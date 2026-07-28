package harnessengine

// Batch evaluation over a golden set — the Task Registry (#2) turned into a real evaluation
// registry: instead of MMLU/HumanEval datasets, refinement cases with the trajectory and
// expected keys. Purely deterministic (0 tokens): compares each run's recorded evidence
// against the case's expectation and aggregates the pass rate.

// GoldenCase is a golden-set case: the expectation the recorded evidence is measured
// against. ExpectPass = false marks an intentional NEGATIVE case — a run that MUST fail the
// metrics (e.g. perfect trajectory but missing content), used to prove the evaluators catch
// the failure. Default is true.
type GoldenCase struct {
	Id                 string
	Description        string
	ExpectedTrajectory []string
	RequiredKeys       []string
	ExpectPass         bool
}

// CaseResult holds the deterministic scores for one case. Passed requires a full match on
// all metrics; Ok is the suite's verdict — did the case behave as the golden set expected
// (an intentional negative case is Ok exactly when Passed is false).
type CaseResult struct {
	Id           string
	Scores       []Score
	ExpectedPass bool
}

// Passed reports whether every score reached the maximum.
func (c CaseResult) Passed() bool {
	for _, s := range c.Scores {
		if !s.Passed() {
			return false
		}
	}
	return true
}

// Ok reports whether the case behaved as the golden set expected.
func (c CaseResult) Ok() bool {
	return c.Passed() == c.ExpectedPass
}

// BatchResult aggregates a batch: fraction of cases that behaved as expected (CI-ready).
type BatchResult struct {
	Cases []CaseResult
}

// Total is the number of evaluated cases.
func (b BatchResult) Total() int {
	return len(b.Cases)
}

// PassedCount is the number of cases whose Ok() is true.
func (b BatchResult) PassedCount() int {
	count := 0
	for _, c := range b.Cases {
		if c.Ok() {
			count++
		}
	}
	return count
}

// PassRate is PassedCount / Total, or 0 when there are no cases.
func (b BatchResult) PassRate() float64 {
	if b.Total() == 0 {
		return 0.0
	}
	return float64(b.PassedCount()) / float64(b.Total())
}

// EvaluateCase scores a single golden case against the recorded trace and final state.
func EvaluateCase(golden GoldenCase, trace []TraceEntry, finalState HarnessState) CaseResult {
	return CaseResult{
		Id: golden.Id,
		Scores: []Score{
			Trajectory(golden.ExpectedTrajectory, CommandsOf(trace, false)),
			StepBudget(trace),
			Completeness(finalState, golden.RequiredKeys),
		},
		ExpectedPass: golden.ExpectPass,
	}
}

// EvaluatorRun bundles a golden case with the run evidence to score.
type EvaluatorRun struct {
	Golden GoldenCase
	Trace  []TraceEntry
	State  HarnessState
}

// EvaluateAllCases scores every run in the batch.
func EvaluateAllCases(runs []EvaluatorRun) BatchResult {
	cases := make([]CaseResult, len(runs))
	for i, r := range runs {
		cases[i] = EvaluateCase(r.Golden, r.Trace, r.State)
	}
	return BatchResult{Cases: cases}
}
