"""Deterministic evaluators (Exact Match, Regex, Trajectory) that do NOT need an LLM.
Run in-process over trace/harness_state, cost zero tokens, and serve as a gate: only
when they pass is it worth escalating to the LLM judge (a saving under the token budget).
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from harness_engine.harness_state import HarnessState
from harness_engine.trace import TraceEntry, TraceOutcome


@dataclass(frozen=True)
class Score:
    """Score for a metric in [0,1]. `passed` requires a full match."""

    metric: str
    value: float
    detail: str = ""

    @property
    def passed(self) -> bool:
        return self.value >= 1.0


def exact_match(expected: str, actual: str) -> Score:
    return Score("exact_match", 1.0 if _norm(expected) == _norm(actual) else 0.0,
                 f'expected="{expected}" got="{actual}"')


def matches_regex(pattern: str, actual: str) -> Score:
    return Score("regex", 1.0 if re.search(pattern, actual) else 0.0, pattern)


def trajectory(expected: list[str], actual: list[str]) -> Score:
    """Fraction of the expected prefix that matched, in order. A step out of sequence cuts
    the count there — trajectory is about the path, not the set."""
    matched = 0
    for e, a in zip(expected, actual):
        if e != a:
            break
        matched += 1

    value = 1.0 if len(expected) == 0 else matched / len(expected)
    return Score("trajectory", value, f"{matched}/{len(expected)} steps in expected order")


def completeness(state: HarnessState, required_keys: list[str]) -> Score:
    """Were all expected domain keys filled in the final state?"""
    filled = sum(1 for k in required_keys if state.data.get(k, "").strip())
    value = 1.0 if len(required_keys) == 0 else filled / len(required_keys)
    return Score("completeness", value, f"{filled}/{len(required_keys)} keys filled")


def step_budget(trace_entries: list[TraceEntry]) -> Score:
    """Ended in `TraceOutcome.STOP` without hitting the step ceiling nor the time ceiling
    (`TraceOutcome.TIMEOUT`) — both would be indistinguishable from a simply-incomplete
    trajectory if not checked separately."""
    hit_budget = any(e.outcome == TraceOutcome.BUDGET for e in trace_entries)
    hit_timeout = any(e.outcome == TraceOutcome.TIMEOUT for e in trace_entries)
    terminated = any(e.outcome == TraceOutcome.STOP for e in trace_entries)

    if hit_budget:
        detail = "cut off by the step ceiling"
    elif hit_timeout:
        detail = "cut off by the time ceiling (timeout)"
    elif terminated:
        detail = "completed within budget"
    else:
        detail = "did not finish"

    return Score("budget", 1.0 if not hit_budget and not hit_timeout and terminated else 0.0, detail)


def commands_of(trace_entries: list[TraceEntry], include_errors: bool = False) -> list[str]:
    """Trace commands in order, by default skipping corrective-error turns."""
    return [e.command for e in trace_entries if include_errors or e.outcome != TraceOutcome.ERROR]


def _norm(value: str) -> str:
    return value.strip()
