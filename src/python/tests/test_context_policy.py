from harness_engine import context_policy, harness_config, state_store
from harness_engine.context_policy import ContextUsage


def test_politica_adaptativa_reseta_no_primeiro_e_no_threshold(monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)
    tmp_path.joinpath("harness.json").write_text(
        '{"contextResetMode":"adaptive","contextResetThreshold":0.7,"contextFallbackFeatures":1}'
    )
    harness_config.reset()
    state_store.reset()

    assert context_policy.new_feature_prefix().startswith("=== NEW SESSION")
    context_policy.observe(ContextUsage(context_window_tokens=100, context_used_tokens=50))
    assert context_policy.new_feature_prefix() == ""
    context_policy.observe(ContextUsage(context_window_tokens=100, context_used_tokens=80))
    assert context_policy.new_feature_prefix().startswith("=== NEW SESSION")


def test_context_usage_lido_do_ambiente(monkeypatch):
    monkeypatch.setenv(
        "HARNESS_CONTEXT_USAGE_JSON",
        '{"contextWindowTokens":100,"contextUsedTokens":70,"source":"host"}',
    )

    usage = ContextUsage.from_environment()

    assert usage is not None
    assert usage.context_window_tokens == 100
    assert usage.context_used_tokens == 70
