"""Driver-agnostic adaptive context reset policy."""

from __future__ import annotations

from dataclasses import dataclass
import json
import os

from harness_engine import harness_config, state_store


@dataclass(frozen=True)
class ContextUsage:
    schema: str = ""
    session_id: str = ""
    context_window_tokens: int = 0
    context_used_tokens: int = 0
    source: str = ""

    def to_dict(self) -> dict[str, object]:
        return {
            "schema": self.schema,
            "sessionId": self.session_id,
            "contextWindowTokens": self.context_window_tokens,
            "contextUsedTokens": self.context_used_tokens,
            "source": self.source,
        }

    @staticmethod
    def from_dict(payload: object) -> "ContextUsage | None":
        if not isinstance(payload, dict):
            return None
        integer_fields = ("contextWindowTokens", "contextUsedTokens")
        if any(not isinstance(payload.get(key, 0), int) or isinstance(payload.get(key, 0), bool) for key in integer_fields):
            return None
        string_fields = ("schema", "sessionId", "source")
        if any(key in payload and not isinstance(payload[key], str) for key in string_fields):
            return None
        return ContextUsage(
            schema=payload.get("schema", ""),
            session_id=payload.get("sessionId", ""),
            context_window_tokens=payload.get("contextWindowTokens", 0),
            context_used_tokens=payload.get("contextUsedTokens", 0),
            source=payload.get("source", ""),
        )

    @staticmethod
    def from_environment() -> "ContextUsage | None":
        raw = os.environ.get("HARNESS_CONTEXT_USAGE_JSON", "").strip()
        if not raw:
            return None
        try:
            return ContextUsage.from_dict(json.loads(raw))
        except (json.JSONDecodeError, TypeError):
            return None


_BOUNDARY_KEY = "context_boundary_seen"
_FEATURES_KEY = "context_features"
_RATIO_KEY = "context_ratio"
_USAGE_SEEN_KEY = "context_usage_seen"


def observe(usage: ContextUsage | None) -> None:
    if usage is None or usage.context_window_tokens <= 0 or usage.context_used_tokens < 0:
        return
    ratio = min(max(usage.context_used_tokens / usage.context_window_tokens, 0.0), 1.0)
    state_store.set(_RATIO_KEY, f"{ratio:.6f}")
    state_store.set(_USAGE_SEEN_KEY, "true")


def new_feature_prefix() -> str:
    reset = _should_reset()
    state_store.set(_BOUNDARY_KEY, "true")
    if reset:
        state_store.set(_FEATURES_KEY, "1")
        state_store.set(_RATIO_KEY, "0")
        state_store.set(_USAGE_SEEN_KEY, "false")
        return "=== NEW SESSION (clean context) ===\n\n"
    features = _read_int(_FEATURES_KEY, 0) + 1
    state_store.set(_FEATURES_KEY, str(features))
    return ""


def _should_reset() -> bool:
    config = harness_config.current()
    mode = config.context_reset_mode.strip().lower()
    if mode == "never":
        return False
    if mode == "per-feature":
        return True
    if state_store.get(_BOUNDARY_KEY) is None:
        return True
    ratio = state_store.get(_RATIO_KEY)
    if ratio is not None:
        try:
            if float(ratio) >= config.context_reset_threshold:
                return True
        except ValueError:
            pass
    return state_store.get(_USAGE_SEEN_KEY) != "true" and _read_int(_FEATURES_KEY, 0) >= config.context_fallback_features


def _read_int(key: str, fallback: int) -> int:
    value = state_store.get(key)
    try:
        parsed = int(value) if value is not None else fallback
        return parsed if parsed >= 0 else fallback
    except ValueError:
        return fallback
