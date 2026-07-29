"""Typed harness errors."""

from __future__ import annotations


class HarnessTimeoutError(Exception):
    """A step's execution timeout was exceeded (see harness_config.timeout_ms). Raised
    and caught inside task_registry: becomes a stderr diagnostic + "stop" on stdout — the
    same graceful-termination contract as the other guards (step and cost ceilings)."""

    def __init__(self, timeout_ms: int):
        super().__init__(f"task execution exceeded the {timeout_ms}ms timeout; stopping.")
