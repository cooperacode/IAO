"""Data contract exchanged between the driver (agent) and the state machine.

The model returns this envelope as JSON; the engine dispatches by `value`.

There is no tokens field: the typical driver is an LLM with no access to its own
request's `usage`, so any self-reported count would be confabulated. The cost ceiling
only uses measures the engine attests itself (steps and instruction chars — see
`task_registry`); real tokens live in the caller's billing metadata.
"""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass

from harness_engine.context_policy import ContextUsage


class EnvelopeType:
    """Protocol signals carried in `Envelope.type`."""

    TEXT = "text"
    TOOL = "tool"
    COMMAND = "command"
    ERROR = "error"


@dataclass(frozen=True)
class Envelope:
    type: str
    value: str
    # Tuple (not list): immutable and comparable by value for free — C# needs manual
    # Equals/GetHashCode because arrays compare by reference; not the case here.
    args: tuple[str, ...] = ()
    context: dict[str, str] | None = None
    context_usage: ContextUsage | None = None

    def to_json(self) -> str:
        data: dict[str, object] = {
            "type": self.type,
            "value": self.value,
            "args": list(self.args),
        }
        if self.context is not None:
            data["context"] = self.context
        if self.context_usage is not None:
            data["contextUsage"] = self.context_usage.to_dict()
        return json.dumps(data, separators=(",", ":"))

    @staticmethod
    def parse(value: str) -> "Envelope | None":
        """Tolerant parse: accepts markdown fences and surrounding text around the JSON object."""
        return Envelope._try_parse(value)

    @staticmethod
    def _try_parse(value: str) -> "Envelope | None":
        try:
            if value is None or not value.strip():
                raise ValueError("The JSON envelope cannot be null or empty.")

            root = json.loads(Envelope._sanitize(value))
            if not isinstance(root, dict):
                raise ValueError("The envelope payload must be a JSON object.")

            type_ = root.get("type") or ""
            envelope_value = root.get("value") or ""
            if not isinstance(type_, str) or not isinstance(envelope_value, str):
                raise TypeError("'type' and 'value' must be strings.")

            args_raw = root.get("args")
            args: tuple[str, ...] = ()
            if isinstance(args_raw, list):
                collected: list[str] = []
                for item in args_raw:
                    if not isinstance(item, str):
                        raise TypeError("each item in 'args' must be a string.")
                    if item.strip():
                        collected.append(item)
                args = tuple(collected)

            context_raw = root.get("context")
            context: dict[str, str] | None = None
            if isinstance(context_raw, dict):
                context = {}
                for key, val in context_raw.items():
                    if not isinstance(val, str):
                        raise TypeError("each value in 'context' must be a string.")
                    context[str(key)] = val

            context_usage = ContextUsage.from_dict(root.get("contextUsage"))

            return Envelope(type_, envelope_value, args, context, context_usage)
        except Exception as ex:
            # Diagnostics go to stderr — stdout is the harness transport channel (the
            # driver reads stdout as the next instruction) and must not be polluted.
            print(ex, file=sys.stderr)
            return None

    # Models frequently wrap the JSON in markdown fences (```json … ```) or add
    # surrounding text. Normalize to the raw JSON object before parsing.
    @staticmethod
    def _sanitize(value: str) -> str:
        v = value.strip()

        if v.startswith("```"):
            first_newline = v.find("\n")
            if first_newline >= 0:
                v = v[first_newline + 1 :]

            closing_fence = v.rfind("```")
            if closing_fence >= 0:
                v = v[:closing_fence]

            v = v.strip()

        start = v.find("{")
        end = v.rfind("}")
        if start >= 0 and end > start:
            v = v[start : end + 1]

        return v
