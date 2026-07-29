"""State persisted between invocations: step counter + accumulated domain data."""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class HarnessState:
    step: int
    data: dict[str, str] = field(default_factory=dict)

    # Accumulated cost of the run, input to the cost ceiling (see task_registry).
    cost_chars: int = 0

    # Driver context (e.g. {"driver": "claude code"}) captured in the `start` envelope —
    # survives between invocations so prompt_formatter can reinject it into every output
    # without each task having to pass it along manually.
    context: dict[str, str] | None = None

    def to_dict(self) -> dict[str, object]:
        result: dict[str, object] = {
            "step": self.step,
            "data": self.data,
            "costChars": self.cost_chars,
        }
        if self.context is not None:
            result["context"] = self.context
        return result

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "HarnessState":
        data = payload.get("data")
        return HarnessState(
            step=int(payload.get("step", 0) or 0),
            data=dict(data) if isinstance(data, dict) else {},
            cost_chars=int(payload.get("costChars", 0) or 0),
            context=dict(payload["context"]) if isinstance(payload.get("context"), dict) else None,
        )
