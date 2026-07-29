"""Batch evaluation over a golden set: purely deterministic (0 tokens) — compares each
run's recorded evidence against the case's expectation and aggregates the pass rate.
"""

from __future__ import annotations

from dataclasses import dataclass

from harness_engine import evaluators
from harness_engine.evaluators import Score
from harness_engine.golden_case_store import GoldenCase
from harness_engine.harness_state import HarnessState
from harness_engine.trace import TraceEntry


@dataclass(frozen=True)
class CaseResult:
    """Deterministic scores for one case. `passed` requires a full match across metrics;
    `ok` is the suite's verdict — the case behaved as the golden set expected (an
    intentional negative case is `ok` precisely when `passed` is false)."""

    id: str
    scores: tuple[Score, ...]
    expected_pass: bool = True

    @property
    def passed(self) -> bool:
        return all(s.passed for s in self.scores)

    @property
    def ok(self) -> bool:
        return self.passed == self.expected_pass


@dataclass(frozen=True)
class BatchResult:
    """Batch aggregate: fraction of cases that behaved as expected (CI-ready)."""

    cases: tuple[CaseResult, ...]

    @property
    def total(self) -> int:
        return len(self.cases)

    @property
    def passed_count(self) -> int:
        return sum(1 for c in self.cases if c.ok)

    @property
    def pass_rate(self) -> float:
        return 0.0 if self.total == 0 else self.passed_count / self.total


def evaluate(golden: GoldenCase, trace_entries: list[TraceEntry], final_state: HarnessState) -> CaseResult:
    return CaseResult(
        golden.id,
        (
            evaluators.trajectory(list(golden.expected_trajectory), evaluators.commands_of(trace_entries)),
            evaluators.step_budget(trace_entries),
            evaluators.completeness(final_state, list(golden.required_keys)),
        ),
        golden.expect_pass,
    )


def evaluate_all(
    runs: list[tuple[GoldenCase, list[TraceEntry], HarnessState]],
) -> BatchResult:
    return BatchResult(tuple(evaluate(golden, trace_entries, state) for golden, trace_entries, state in runs))
