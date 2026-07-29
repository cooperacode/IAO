"""Test isolation: each test runs in its own `tmp_path` (chdir), instead of the C# side's
`[assembly: CollectionBehavior(DisableTestParallelization = true)]` (needed there because
the stores use a fixed path relative to the cwd shared across tests). Here isolation is
real per test, not just serialization — the tests are free to run in parallel (e.g.
pytest-xdist) if that's ever needed.
"""

from __future__ import annotations

import pytest

from harness_engine import harness_config


@pytest.fixture(autouse=True)
def isolated_cwd(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    # In C# each harness invocation is a new process (HarnessConfig.Current's cache
    # naturally lasts 1 dispatch); in a long-lived pytest process that's not free — without
    # this, the config loaded in the first test would leak into the following ones.
    harness_config.reset()
    yield
    harness_config.reset()
