from __future__ import annotations

import importlib.util
from pathlib import Path
import sys


def _load_adapter():
    scripts_dir = Path(__file__).resolve().parents[3] / ".harness" / "scripts"
    if str(scripts_dir) not in sys.path:
        sys.path.insert(0, str(scripts_dir))
    path = scripts_dir / "claude_context_usage.py"
    spec = importlib.util.spec_from_file_location("claude_context_usage", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_claude_context_includes_cached_input_tokens():
    adapter = _load_adapter()

    assert adapter.context_input_tokens(
        {
            "input_tokens": 2,
            "cache_creation_input_tokens": 820,
            "cache_read_input_tokens": 97_345,
            "output_tokens": 1_210,
        }
    ) == 98_167


def test_claude_context_accepts_transcripts_without_cache_fields():
    adapter = _load_adapter()

    assert adapter.context_input_tokens({"input_tokens": 321}) == 321


def test_claude_context_rejects_invalid_token_counts():
    adapter = _load_adapter()

    assert adapter.context_input_tokens({"input_tokens": 2, "cache_read_input_tokens": "97345"}) is None


def test_latest_context_usage_publishes_full_input_context(monkeypatch):
    adapter = _load_adapter()
    event = (
        "session-1",
        "main",
        "claude-sonnet-5",
        {
            "input_tokens": 2,
            "cache_creation_input_tokens": 820,
            "cache_read_input_tokens": 97_345,
        },
        "2026-08-01T15:00:00Z",
    )
    monkeypatch.setattr(adapter.claude_usage, "default_project_dir", lambda: Path("/tmp/claude"))
    monkeypatch.setattr(adapter.claude_usage, "iter_usage_events", lambda *args, **kwargs: iter([event]))

    result = adapter.latest_context_usage("session-1", 200_000)

    assert result is not None
    assert result["contextUsedTokens"] == 98_167
