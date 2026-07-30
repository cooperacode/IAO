# Quickstart

Fast path to build a harness package and start developing with it. For the full pattern explanation, see [README.md](README.md).

## Prerequisites

Only the engine you choose to package needs its toolchain installed:

- **dotnet**: SDK supporting `net10.0`
- **rust**: stable toolchain + `cargo`
- **go**: Go toolchain (`go build`)
- **python**: 3.11+ interpreter on the target machine's PATH

## 1. Build a package

```bash
./package.sh --engine <dotnet|python|rust|go> [--os <rid>] --ide <claude|copilot|devin|codex> [--version <v>]

# or run with no flags for an interactive menu
./package.sh
```

- `--os` (RID) only applies to `--engine dotnet` — Native AOT compiles per OS (`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`).
- `rust`, `go`, and `python` packages are built for/run on the host you run this script on (no cross-compile).

This produces a self-contained folder in `dist/`:

```
dist/flows-<rid>-v<version>/          # --engine dotnet
dist/flows-rust-<host-rid>-v<version>/ # --engine rust
dist/flows-go-<host-rid>-v<version>/   # --engine go
dist/flows-python-v<version>/          # --engine python
```

Each package includes the engine binary (or Python source), `skills/`, `scripts/`, the chosen IDE adapter, and a `START-HERE.md`.

## 2. Move the package into your project

Copy the generated `dist/flows-*-v<version>/` folder's contents into the root of the project you want to develop:

```bash
cp -R dist/flows-*-v<version>/. /path/to/your-project/
```

## 3. Configure the harness

Before starting the agent, review `harness.json` at the project root. It is
optional: a missing or invalid file falls back to the built-in defaults. The
configuration is read on every runner invocation, so changes apply on the
next turn.

The shipped baseline is:

```json
{
  "maxSteps": 12,
  "maxInstructionChars": 0,
  "docsMaxChars": 40000,
  "docsFolder": "docs",
  "timeoutMs": 30000,
  "contextResetMode": "adaptive",
  "contextResetThreshold": 0.7,
  "contextFallbackFeatures": 1
}
```

| Field | Meaning | Accepted values and guidance |
|---|---|---|
| `maxSteps` | Global maximum number of harness turns. | Positive integer. The Development flow supplies its own larger step budget for its feature loop; this value mainly governs the generic host and other flows. |
| `maxInstructionChars` | Maximum size of an instruction emitted to the driver. | Non-negative integer; `0` disables this guard. Use it to prevent oversized prompts from consuming the context window. |
| `docsMaxChars` | Maximum number of characters read from the documents folder. | Positive integer. Increase only when the brief genuinely needs it; prefer splitting large documents. |
| `docsFolder` | Folder containing the brief files read by `start`. | Relative or absolute path. The default is `docs`; supported source files are Markdown and text documents. |
| `timeoutMs` | Per-task execution timeout. | `0` disables the timeout; positive values are milliseconds. Values above five minutes are clamped. `30000` means 30 seconds. |
| `contextResetMode` | Policy for requesting a clean driver context between features. | `adaptive`, `per-feature`, or `never`. `adaptive` is recommended when the driver can report usage. |
| `contextResetThreshold` | Usage ratio that triggers an adaptive reset. | Number greater than `0` and at most `1`; `0.7` means 70% of the reported context window. |
| `contextFallbackFeatures` | Number of feature boundaries used as a deterministic fallback when no context telemetry is available. | Positive integer. `1` preserves a per-feature boundary when telemetry is absent. |

Recommended choices:

- Keep `maxInstructionChars` at `0` unless the environment has a strict prompt
  budget.
- Keep `timeoutMs` around `30000` for normal local commands; raise it only for
  a known slow build/test command, and never beyond the five-minute ceiling.
- Use `adaptive` with `contextResetThreshold: 0.7` when the driver supplies
  `contextUsage` or `HARNESS_CONTEXT_USAGE_JSON`.
- Use `per-feature` when every feature must start in a clean context regardless
  of telemetry. Use `never` only when the driver is intentionally kept in one
  continuous session.
- Keep `docsMaxChars` bounded and put implementation details in the target
  repository rather than expanding the planning brief indefinitely.

`harness.json` does not contain the verification command or target directory.
Those values are captured by `plan` in `.harness/run_config.json`:

```json
{
  "verifyCmd": "dotnet test",
  "targetDir": "src/app",
  "runId": "generated-by-the-harness"
}
```

For trusted unattended integrations, the initializer can be overridden outside
the model response with `HARNESS_TARGET_DIR` and `HARNESS_VERIFY_CMD`. The
per-task timeout can likewise be overridden by the parent process with
`HARNESS_TIMEOUT_MS`. Do not put credentials or provider-specific token data in
`harness.json`.

## 4. Start development

Development is always driven by the IDE agent, not by hand on the command line. Open `your-project/` in the IDE you packaged for and follow `START-HERE.md` (copied into the package root):

| IDE | Adapter | How to start |
|---|---|---|
| Claude Code | `.claude/agents/development.agent.md` | `/agents` → **development**, then ask *"Develop: \<project goal\>"* |
| GitHub Copilot | `.github/prompts/development.prompt.md` | Select the **development** prompt file, then ask *"Develop: \<project goal\>"* |
| Devin | `.devin/workflows/development.md` | Invoke `/development`, then ask *"Develop: \<project goal\>"* |
| Codex | `.codex/agents/development.toml` | Ask to use the custom **development** agent for *"Develop: \<project goal\>"* |

The agent drives `./run-development.sh` itself, one feature at a time, until every feature passes verification.
