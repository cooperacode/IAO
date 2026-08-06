"""Fixed harness variables, externalized in a `harness.json` at the repo root.
Centralizing them here lets each flow/environment adjust the ceilings without changing
code. Absent or unreadable → falls back to the defaults: config is optional input, it
cannot bring the run down.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, replace
from pathlib import Path

from harness_engine import harness_log, path_resolver

_FILE_PATH = "harness.json"

# Hard ceiling on timeout_ms, regardless of source (harness.json OR the env var below).
# harness.json lives in the working directory the supervised agent itself controls:
# without this ceiling, the agent could edit the file to grant itself an arbitrarily high
# timeout and never be cut off by the time guard (see task_registry).
_MAX_ALLOWED_TIMEOUT_MS = 10 * 60_000
_MIN_ENABLED_TIMEOUT_MS = 1

# When set, overrides harness.json's timeout_ms. Unlike the file, the env var is set by
# the parent process that invokes each harness step — outside the working directory the
# supervised agent controls — so it cannot be self-edited by the same agent the timeout
# is meant to contain.
_TIMEOUT_MS_ENV_VAR = "HARNESS_TIMEOUT_MS"


@dataclass(frozen=True)
class HarnessConfig:
    max_steps: int
    max_instruction_chars: int
    docs_max_chars: int
    docs_folder: str
    timeout_ms: int
    context_reset_mode: str
    context_reset_threshold: float
    context_fallback_features: int

    def to_dict(self) -> dict[str, object]:
        return {
            "maxSteps": self.max_steps,
            "maxInstructionChars": self.max_instruction_chars,
            "docsMaxChars": self.docs_max_chars,
            "docsFolder": self.docs_folder,
            "timeoutMs": self.timeout_ms,
            "contextResetMode": self.context_reset_mode,
            "contextResetThreshold": self.context_reset_threshold,
            "contextFallbackFeatures": self.context_fallback_features,
        }


# Step ceiling: prevents an infinite loop that would burn tokens indefinitely.
# max_instruction_chars = 0 disables the cost ceiling (only the step one applies).
# timeout_ms is always enabled; zero/missing values fall back to the 10-minute default.
DEFAULT = HarnessConfig(
    max_steps=12,
    max_instruction_chars=0,
    docs_max_chars=40_000,
    docs_folder="specs",
    timeout_ms=10 * 60_000,
    context_reset_mode="adaptive",
    context_reset_threshold=0.70,
    context_fallback_features=1,
)

_current: HarnessConfig | None = None


def _normalize(config: HarnessConfig) -> HarnessConfig:
    mode = config.context_reset_mode.strip().lower()
    return replace(
        config,
        max_steps=config.max_steps if config.max_steps > 0 else DEFAULT.max_steps,
        max_instruction_chars=max(config.max_instruction_chars, 0),
        docs_max_chars=config.docs_max_chars if config.docs_max_chars > 0 else DEFAULT.docs_max_chars,
        docs_folder=config.docs_folder.strip() if config.docs_folder and config.docs_folder.strip() else DEFAULT.docs_folder,
        # A workspace-controlled config may tune the timeout, but it may not turn the
        # guard off. The terminal latch in task_registry is the second stop mechanism for
        # a timeout that already occurred.
        timeout_ms=min(
            max(config.timeout_ms if config.timeout_ms > 0 else DEFAULT.timeout_ms, _MIN_ENABLED_TIMEOUT_MS),
            _MAX_ALLOWED_TIMEOUT_MS,
        ),
        context_reset_mode=mode if mode in {"adaptive", "per-feature", "never"} else DEFAULT.context_reset_mode,
        context_reset_threshold=min(max(config.context_reset_threshold or DEFAULT.context_reset_threshold, 0.1), 1.0),
        context_fallback_features=config.context_fallback_features if config.context_fallback_features > 0 else DEFAULT.context_fallback_features,
    )


def _apply_timeout_env_override(config: HarnessConfig) -> HarnessConfig:
    """See `_TIMEOUT_MS_ENV_VAR`. Absent/invalid is silently ignored — the same
    tolerance as the rest of the config: it's optional input, it cannot bring the run down."""
    raw = os.environ.get(_TIMEOUT_MS_ENV_VAR)
    if raw is None:
        return config
    try:
        return replace(config, timeout_ms=int(raw))
    except ValueError:
        return config


def load() -> HarnessConfig:
    """Re-reads `harness.json` from disk; any failure returns the defaults."""
    config = DEFAULT
    try:
        path = Path(path_resolver.resolve(_FILE_PATH))
        if path.exists():
            payload = json.loads(path.read_text())
            config = HarnessConfig(
                max_steps=int(payload.get("maxSteps", 0) or 0),
                max_instruction_chars=int(payload.get("maxInstructionChars", 0) or 0),
                docs_max_chars=int(payload.get("docsMaxChars", 0) or 0),
                docs_folder=str(payload.get("docsFolder") or ""),
                timeout_ms=int(payload.get("timeoutMs", 0) or 0),
                context_reset_mode=str(payload.get("contextResetMode") or ""),
                context_reset_threshold=float(payload.get("contextResetThreshold", 0) or 0),
                context_fallback_features=int(payload.get("contextFallbackFeatures", 0) or 0),
            )
    except Exception as ex:
        harness_log.error(f"[HarnessConfig] failed to load; using defaults: {ex}")
        config = DEFAULT

    return _normalize(_apply_timeout_env_override(config))


def reload() -> HarnessConfig:
    """Forces a re-read of `harness.json` — for tests and long-lived drivers."""
    global _current
    _current = load()
    return _current


def reset() -> None:
    """Clears the cache without re-reading — the next `current()` re-reads on demand. In
    C# every harness invocation is a fresh process (the cache naturally lasts 1
    dispatch); in a long-lived pytest process that doesn't hold for free, so the tests
    call this (see conftest.py) to simulate the process boundary between cases."""
    global _current
    _current = None


def current() -> HarnessConfig:
    """Loaded once per process (every harness invocation is a fresh process, so "once"
    means "per loop turn"). Static readers consume it from here without needing the
    config passed as a parameter."""
    global _current
    if _current is None:
        _current = load()
    return _current
