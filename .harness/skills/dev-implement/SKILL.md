---
name: dev-implement
description: "implement exactly the selected feature with the smallest complete, buildable change"
---

# Implement one feature

Implement only the selected feature.

- Use the current feature's description, references, and inline implementation context as the
  feature scope. Use the injected `<design-context>` only for architecture, interfaces,
  diagrams, folder layout, and cross-feature decisions. The full brief and bearings are not
  part of this session's contract, and the design context does not authorize unrelated work.
- Make the smallest complete change; avoid unrelated cleanup or work for later features.
- Add or update the tests needed to demonstrate the selected behavior.
- Leave the target building. Keep long command output in `.harness/logs/` and inspect only
  the relevant excerpt.
- If an undeclared dependency blocks the feature, add only the minimum necessary to unblock
  it and keep the rest out of scope.
