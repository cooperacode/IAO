---
name: dev-implement
description: "implement exactly the selected feature with the smallest complete, buildable change"
---

# Implement one feature

Implement only the selected feature.

- Use the feature description, references, brief, and bearings only as scope context.
- Make the smallest complete change; avoid unrelated cleanup or work for later features.
- Add or update the tests needed to demonstrate the selected behavior.
- Leave the target building. Keep long command output in `.harness/logs/` and inspect only
  the relevant excerpt.
- If an undeclared dependency blocks the feature, add only the minimum necessary to unblock
  it and keep the rest out of scope.
