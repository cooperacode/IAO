# Changelog

All notable changes to this project are documented in this file.

The format follows [Semantic Versioning](https://semver.org/) and entries are
organized using categories such as `Added`, `Changed`, `Fixed`, and `Breaking`.

## [Unreleased]

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
