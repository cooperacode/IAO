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

## 3. Start development

Development is always driven by the IDE agent, not by hand on the command line. Open `your-project/` in the IDE you packaged for and follow `START-HERE.md` (copied into the package root):

| IDE | Adapter | How to start |
|---|---|---|
| Claude Code | `.claude/agents/development.agent.md` | `/agents` → **development**, then ask *"Develop: \<project goal\>"* |
| GitHub Copilot | `.github/prompts/development.prompt.md` | Select the **development** prompt file, then ask *"Develop: \<project goal\>"* |
| Devin | `.devin/workflows/development.md` | Invoke `/development`, then ask *"Develop: \<project goal\>"* |
| Codex | `.codex/agents/development.toml` | Ask to use the custom **development** agent for *"Develop: \<project goal\>"* |

The agent drives `./run-development.sh` itself, one feature at a time, until every feature passes verification.
