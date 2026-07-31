"""Keys of `state_store.data` used by `tasks.py` and `prompts.py` — its own module
(instead of being defined in either one) so as not to create a circular import between
them (`tasks.py` already imports `prompts`)."""

from __future__ import annotations

CURRENT_FEATURE_ID = "current_feature_id"
CURRENT_FEATURE_TITLE = "current_feature_title"
CURRENT_FEATURE_SUMMARY = "current_feature_summary"
CURRENT_FEATURE_VERIFY = "current_feature_verify"
CURRENT_BEARINGS = "current_bearings"
FEATURE_STEPS = "feature_steps"

# Not a state_store key — it's the brief artifact's name in artifact_store
# (.harness/brief.md). Lives here for the same reason as the keys above: tasks.py and
# prompts.py need the same value, without creating a circular import between them.
BRIEF_ARTIFACT_NAME = "brief"
