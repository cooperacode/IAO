# Changelog

All notable changes to this project are documented in this file.

The format follows [Semantic Versioning](https://semver.org/) and entries are
organized using categories such as `Added`, `Changed`, `Fixed`, and `Breaking`.

## [0.5.0] - 2026-08-06

### Added

- Support for diamond-shaped agent hierarchies, allowing for more complex task delegation in verification pipelines.

### Changed

- Improved handling of the `=== NEW SESSION (clean context) ===` marker to ensure proper delegation of tasks to sub-agents.

### Fixed

- Resolved an issue where the harness would not properly delegate tasks to sub-agents when the `=== NEW SESSION (clean context) ===` marker was present.

## [Unreleased]

### Added

- Kimi Code CLI as a supported driver: `.kimi/agents/{development,specification}.md`
  adapters, wired into `.harness/gui/drivers.py` (via Kimi's native `--agent-file`
  resolution), `package.sh`, and `.harness/scripts/check-development-contracts.sh`.
  `validated: True` — the exact command shape was spiked twice against a real,
  authenticated install (bare and with `--agent-file`), headless, exit code 0.
- `.harness/scripts/kimi_usage.py`: extracts token usage and estimated cost from
  local Kimi Code CLI sessions (`~/.kimi-code/sessions/**/agents/*/wire.jsonl`
  `usage.record` events), wired into `harness_cost_correlate.py`
  (`--usage-source kimi`) and `session-report/generate_report.py --driver kimi` —
  closes the cost-report gap for the Kimi driver. Pricing is a public-API-based
  estimate (Kimi's default managed/OAuth plan bills flat-rate, not per token),
  matched to Moonshot's raw API model prices by context-window/name
  correspondence; one model alias (`kimi-code/k3-256k`) has no confident match
  and is left unpriced.
- `.harness/scripts/kimi_context_usage.py`: emits the `iao.context.v1` contract
  (`HARNESS_CONTEXT_USAGE_JSON`) for Kimi's adaptive context-reset policy, read
  from `wire.jsonl`'s `llm.request.maxTokens` (the model's real configured
  context window, unlike Claude's env-var-default fallback) paired with the
  matching `usage.record`'s input total. Wired into a new "Driver telemetry"
  section in `.kimi/agents/{development,specification}.md`.

### Changed

- `package.sh` no longer asks which IDE to package for (`--ide` is now a deprecated,
  ignored no-op kept for backward compatibility): every generated package bundles the
  adapter/prompt and approval config for all five supported IDEs (Claude Code, GitHub
  Copilot, Devin, Codex, Kimi Code CLI), and `.harness/START-HERE.md` documents
  getting-started steps for each of them.
- `package.sh` gained a branded ASCII-logo banner and redesigned interactive menus
  (colored, numbered, with per-option descriptions) for the engine/OS selection.

## [0.3.0] - 2026-08-05

### Fixed

- `PlanRetryPrompt` (Go, Python, Rust, .NET) now reattaches the persisted
  `brief` artifact when asking the driver to resend an unparsed feature list,
  instead of relying on the driver still holding the original brief in
  context. Without this, a driver whose context had already dropped the
  brief could fall back to planning a generic, unrelated feature.
- Raised the default `docsMaxChars` in `harness.json` from `40000` to
  `10000000` so briefs made of several `specs/` documents are no longer
  silently truncated mid-file before reaching the initializer.

### Added

- Version management based on `VERSION`, Git tags, and automated releases.
- Consistency checks between the repository version and the Python and Rust
  manifests.
- Continuous checks for all four engines and release package generation.

## [0.1.0] - 2026-08-02

### Added

- First formal version of the project, still in an early stage of development.
