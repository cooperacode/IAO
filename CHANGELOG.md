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

### Changed

- `package.sh` no longer asks which IDE to package for (`--ide` is now a deprecated,
  ignored no-op kept for backward compatibility): every generated package bundles the
  adapter/prompt and approval config for all four supported IDEs (Claude Code, GitHub
  Copilot, Devin, Codex), and `.harness/START-HERE.md` documents getting-started steps
  for each of them.
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
