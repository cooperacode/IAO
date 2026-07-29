"""Deterministic, cheap predicates to validate whether the value the driver returned
meets the task's expectation — BEFORE persisting it and advancing the flow. Failed →
task_registry returns a typed corrective error and the driver resends (corrective loop,
not silent termination).

Deep semantic validation is still the LLM judge's job during evaluation; only what is
checkable in code, at zero token cost, lives here.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Callable

from harness_engine.envelope import Envelope

Validator = Callable[[Envelope], "ValidationResult"]


@dataclass(frozen=True)
class ValidationResult:
    ok: bool
    reason: str = ""

    @staticmethod
    def passed() -> "ValidationResult":
        return ValidationResult(True, "")

    @staticmethod
    def fail(reason: str) -> "ValidationResult":
        return ValidationResult(False, reason)


def not_empty(expectation: str) -> Validator:
    """The first arg exists and is not empty/whitespace."""

    def validator(envelope: Envelope) -> ValidationResult:
        return (
            ValidationResult.passed()
            if _first_arg(envelope)
            else ValidationResult.fail(f"The expected argument was empty. Expected: {expectation}.")
        )

    return validator


def min_lines(count: int, expectation: str) -> Validator:
    """The first arg has at least `count` non-empty lines (counting literal `\\n` too)."""

    def validator(envelope: Envelope) -> ValidationResult:
        lines = _lines(_first_arg(envelope))
        return (
            ValidationResult.passed()
            if lines >= count
            else ValidationResult.fail(
                f"The argument has {lines} non-empty line(s), but the task expects at least {count}. "
                f"Expected: {expectation}."
            )
        )

    return validator


def contains_number(expectation: str) -> Validator:
    """The first arg contains at least one number."""

    def validator(envelope: Envelope) -> ValidationResult:
        return (
            ValidationResult.passed()
            if re.search(r"\d", _first_arg(envelope))
            else ValidationResult.fail(f"The argument does not contain any number. Expected: {expectation}.")
        )

    return validator


def matches(pattern: str, expectation: str) -> Validator:
    """The first arg matches the pattern (case-insensitive)."""

    def validator(envelope: Envelope) -> ValidationResult:
        return (
            ValidationResult.passed()
            if re.search(pattern, _first_arg(envelope), re.IGNORECASE)
            else ValidationResult.fail(f"The argument does not match the expected format. Expected: {expectation}.")
        )

    return validator


def all_of(*validators: Validator) -> Validator:
    """Composition: every predicate must pass; the first one that fails supplies the reason."""

    def validator(envelope: Envelope) -> ValidationResult:
        for v in validators:
            result = v(envelope)
            if not result.ok:
                return result
        return ValidationResult.passed()

    return validator


def _first_arg(envelope: Envelope) -> str:
    return envelope.args[0].strip() if envelope.args else ""


# Artifacts travel as a single-line JSON string with literal \n (see the "Compact" warning
# in the flows) — counts both real and escaped line breaks.
def _lines(value: str) -> int:
    parts = re.split(r"\n|\\n", value)
    return sum(1 for part in parts if part.strip())
