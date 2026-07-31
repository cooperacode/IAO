#!/usr/bin/env bash
# Packages the development flow into a self-contained package, choosing the ENGINE
# (--engine), the operating system (RID, only for --engine dotnet) and the IDE. The
# package includes the chosen engine, the runtime skills and the matching IDE adapter.
# Generates:
#
#   --engine dotnet → dist/flows-<rid>-v<version>/
#     bin/Flows.Development           # native binary (Native AOT; self-contained if AOT fails)
#     skills/                         # skills injected at runtime
#     scripts/                        # usage/correlate — dependency of the cost report
#     run-development.sh (+ .cmd win) # development wrapper → ./bin
#     <IDE adapter at its expected path>
#     <IDE approval config>           # runs the wrapper with no per-command prompt
#     START-HERE.md                   # how to run the flow in the chosen IDE
#
#   --engine python → dist/flows-python-v<version>/
#     engine/harness_engine, engine/flows_development  # Python engine (source, no build)
#     skills/, scripts/, run-development.sh (+ .cmd)    # same layout, requires python3/python in PATH
#     <IDE adapter>, <approval config>, START-HERE.md
#
#   --engine rust → dist/flows-rust-<host-rid>-v<version>/
#     bin/flows_development           # native binary (cargo build --release --bin flows_development)
#     skills/, scripts/, run-development.sh (+ .cmd win) # same layout as dotnet, wrapper → ./bin
#     <IDE adapter>, <approval config>, START-HERE.md
#
#   --engine go → dist/flows-go-<host-rid>-v<version>/
#     bin/flowsdevelopment            # native binary (go build ./flowsdevelopment)
#     skills/, scripts/, run-development.sh (+ .cmd win) # same layout as rust, wrapper → ./bin
#     <IDE adapter>, <approval config>, START-HERE.md
#
# Usage:
#   ./package.sh --engine <dotnet|python|rust|go> [--os <rid>] --ide <claude|copilot|devin|codex> [--version <v>]
#   ./package.sh                     # interactive mode (menus)
#
# --os/--rid only applies to --engine dotnet (Native AOT compiles per OS). The python
# engine runs the same on any OS with the interpreter in PATH — there's no RID for it. The
# rust and go engines also ignore --os: neither `cargo build --release` nor `go build`
# cross-compile here, so the binary comes out native to the host this script ran on — the
# <host-rid> in the package name is auto-detected (uname -s/-m), not selectable.
# RIDs (--engine dotnet only): osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

ENGINES=(dotnet python rust go)
RIDS=(osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64)
IDES=(claude copilot devin codex)
# On this branch the packaged project is just the development flow.
FLOWS=(development)

ENGINE=""
RID=""
IDE=""
VERSION="1.0.0"

usage() {
  echo "usage: ./package.sh --engine <dotnet|python|rust|go> [--os <rid>] --ide <claude|copilot|devin|codex> [--version <v>]"
  echo "engines: ${ENGINES[*]}"
  echo "RIDs (--engine dotnet only): ${RIDS[*]}"
}

# Auto-detects a RID-like value to name the --engine rust/go package (neither cargo nor
# go build cross-compile here; the binary only runs on the same OS/architecture as the
# host that ran this script).
host_rid() {
  local os arch
  case "$(uname -s)" in
    Darwin) os="osx";;
    Linux) os="linux";;
    MINGW*|MSYS*|CYGWIN*) os="win";;
    *) os="unknown";;
  esac
  case "$(uname -m)" in
    arm64|aarch64) arch="arm64";;
    x86_64|amd64) arch="x64";;
    *) arch="unknown";;
  esac
  echo "$os-$arch"
}

# ---- argument parsing ----
while [[ $# -gt 0 ]]; do
  case "$1" in
    --engine)   ENGINE="${2:-}"; shift 2;;
    --os|--rid) RID="${2:-}"; shift 2;;
    --ide)      IDE="${2:-}"; shift 2;;
    --version|-v) VERSION="${2:-}"; shift 2;;
    -h|--help)  usage; exit 0;;
    *) echo "unknown argument: $1" >&2; usage; exit 1;;
  esac
done

contains() { local x; for x in "${@:2}"; do [[ "$x" == "$1" ]] && return 0; done; return 1; }

# ---- per-flow metadata ----
project_for() { case "$1" in
  development) echo "src/dotnet/Flows.Development/Flows.Development.csproj";;
esac; }
assembly_for() { case "$1" in
  development) echo "Flows.Development";;
esac; }
wrapper_for() { case "$1" in
  development) echo "run-development.sh";;
esac; }
# Each engine owns the wrapper installed at the package root. Keeping these templates
# beside their implementation avoids executable entry points at the repository root.
wrapper_source_for() {
  local engine="$1" flow="$2"
  case "$engine:$flow" in
    dotnet:development) echo "src/dotnet/run-development.sh";;
    python:development) echo "src/python/run-development-py.sh";;
    rust:development) echo "src/rust/run-development-rs.sh";;
    go:development) echo "src/go/run-development-go.sh";;
  esac
}
# --engine python only: name of the package under src/python/ that implements the flow.
python_module_for() { case "$1" in
  development) echo "flows_development";;
esac; }
# --engine rust only: name of the bin crate's binary under src/rust/ that implements the flow.
rust_bin_for() { case "$1" in
  development) echo "flows_development";;
esac; }
# --engine go only: name of the main package's binary under src/go/ that implements the flow.
go_bin_for() { case "$1" in
  development) echo "flowsdevelopment";;
esac; }
# adapter per IDE+flow → "SRC<TAB>REL" (REL = path expected by the IDE inside the package)
adapter_for() { case "$1:$2" in
  claude:development)  printf '%s\t%s\n' ".claude/agents/development.agent.md"    ".claude/agents/development.agent.md";;
  copilot:development) printf '%s\t%s\n' ".github/prompts/development.prompt.md"  ".github/prompts/development.prompt.md";;
  devin:development)   printf '%s\t%s\n' ".devin/workflows/development.md"        ".devin/workflows/development.md";;
  codex:development)   printf '%s\t%s\n' ".codex/agents/development.toml"         ".codex/agents/development.toml";;
esac; }

# ---- interactive selection when missing ----
if [[ -z "$ENGINE" ]]; then
  echo "Select the harness engine:"
  select e in "${ENGINES[@]}"; do [[ -n "${e:-}" ]] && ENGINE="$e" && break; done
fi
# RID only exists for the dotnet engine (Native AOT compiles per OS); the python engine
# runs the same on any OS with the interpreter in PATH.
if [[ "$ENGINE" == "dotnet" && -z "$RID" ]]; then
  echo "Select the operating system (RID):"
  select r in "${RIDS[@]}"; do [[ -n "${r:-}" ]] && RID="$r" && break; done
fi
if [[ -z "$IDE" ]]; then
  echo "Select the IDE:"
  select i in "${IDES[@]}"; do [[ -n "${i:-}" ]] && IDE="$i" && break; done
fi

# ---- validation ----
contains "$ENGINE" "${ENGINES[@]}" || { echo "invalid engine: '$ENGINE' (use: ${ENGINES[*]})" >&2; exit 1; }
if [[ "$ENGINE" == "dotnet" ]]; then
  contains "$RID" "${RIDS[@]}" || { echo "invalid RID: '$RID' (use: ${RIDS[*]})" >&2; exit 1; }
elif [[ -n "$RID" ]]; then
  if [[ "$ENGINE" == "rust" || "$ENGINE" == "go" ]]; then
    echo "[warning] --os '$RID' ignored: engine '$ENGINE' doesn't cross-compile (native host binary, auto-detected RID)." >&2
  else
    echo "[warning] --os '$RID' ignored: engine 'python' doesn't use a RID (runs on any OS with the interpreter in PATH)." >&2
  fi
  RID=""
fi
contains "$IDE" "${IDES[@]}" || { echo "invalid IDE: '$IDE' (use: ${IDES[*]})" >&2; exit 1; }
[[ -n "$VERSION" ]] || { echo "empty version" >&2; exit 1; }

# the flow's adapter must exist for the chosen IDE
for flow in "${FLOWS[@]}"; do
  IFS=$'\t' read -r src _rel < <(adapter_for "$IDE" "$flow")
  [[ -f "$src" ]] || { echo "adapter not found: $src (ide=$IDE, flow=$flow)" >&2; exit 1; }
  wrapper_src="$(wrapper_source_for "$ENGINE" "$flow")"
  [[ -f "$wrapper_src" ]] || {
    echo "wrapper not found: $wrapper_src (engine=$ENGINE, flow=$flow)" >&2
    exit 1
  }
done

if [[ "$ENGINE" == "dotnet" ]]; then
  # ---- warning: AOT doesn't cross-compile between OSes ----
  HOST_OS="$(uname -s)"
  TARGET_OS="unknown"
  case "$RID" in osx-*) TARGET_OS="Darwin";; linux-*) TARGET_OS="Linux";; win-*) TARGET_OS="Windows";; esac
  if [[ "$TARGET_OS" != "unknown" && "$HOST_OS" != "$TARGET_OS" ]]; then
    echo "[warning] Native AOT compiles for the host OS ($HOST_OS)." >&2
    echo "[warning] Target '$RID' is $TARGET_OS — run this script on that OS (or in a CI) if the publish fails." >&2
  fi

  WINEXT=""; [[ "$RID" == win-* ]] && WINEXT=".exe"
  OUT="dist/flows-$RID-v$VERSION"
elif [[ "$ENGINE" == "rust" ]]; then
  HOSTRID="$(host_rid)"
  echo "[warning] engine 'rust': native binary of the build host ($HOSTRID) — cargo doesn't cross-compile here; run this script on the target's OS/architecture if it's different." >&2
  WINEXT=""; [[ "$HOSTRID" == win-* ]] && WINEXT=".exe"
  OUT="dist/flows-rust-$HOSTRID-v$VERSION"
elif [[ "$ENGINE" == "go" ]]; then
  HOSTRID="$(host_rid)"
  echo "[warning] engine 'go': native binary of the build host ($HOSTRID) — this script doesn't cross-compile (GOOS/GOARCH) by default; run it on the target's OS/architecture if it's different." >&2
  WINEXT=""; [[ "$HOSTRID" == win-* ]] && WINEXT=".exe"
  OUT="dist/flows-go-$HOSTRID-v$VERSION"
else
  WINEXT=""
  OUT="dist/flows-python-v$VERSION"
fi

echo "[package] assembling $OUT …"
rm -rf "$OUT"
mkdir -p "$OUT"
[[ "$ENGINE" == "dotnet" || "$ENGINE" == "rust" || "$ENGINE" == "go" ]] && mkdir -p "$OUT/bin"
cp -R skills "$OUT/skills"
cp harness.json "$OUT/harness.json"   # harness variable config (ceilings, docs)

# scripts/ — dependency of skills/session-report/generate_report.py (REPO_ROOT/"scripts",
# relative to the file's own position inside the package). Without this, the agent's final
# step ("generate the usage and cost report") fails because it can't find scripts/<driver>_usage.py.
mkdir -p "$OUT/scripts"
cp scripts/*.py "$OUT/scripts/"

# ---- python engine: the engine is source, not a build — copy harness_engine (shared
# across flows) just once, outside the per-flow loop (the same pattern as skills/ and
# scripts/ above) ----
if [[ "$ENGINE" == "python" ]]; then
  mkdir -p "$OUT/engine"
  cp -R "src/python/harness_engine" "$OUT/engine/harness_engine"
  find "$OUT/engine/harness_engine" -name "__pycache__" -type d -exec rm -rf {} +
fi

# ---- per flow: engine (.NET build or copy of the Python source), wrapper(s) and adapter ----
AOT_FALLBACK_FLOWS=()
for flow in "${FLOWS[@]}"; do
  wrapper="$(wrapper_for "$flow")"
  wrapper_src="$(wrapper_source_for "$ENGINE" "$flow")"

  if [[ "$ENGINE" == "dotnet" ]]; then
    project="$(project_for "$flow")"
    assembly="$(assembly_for "$flow")"
    bin="$assembly$WINEXT"

    echo "[package] publishing Native AOT — $flow ($RID)…"
    used_fallback=false
    if ! dotnet publish "$project" -c Release -r "$RID" -p:PublishAot=true; then
      # Common failure: the host's native toolchain is missing (e.g. Xcode Command Line
      # Tools/clang on macOS, clang+zlib1g-dev on Linux) or the target RID is for another OS
      # (AOT doesn't cross-compile — see warning above). Fallback: self-contained publish
      # WITHOUT AOT — still runs without requiring .NET installed on the target machine
      # (the runtime ships embedded in the package), it just swaps the native binary for a
      # larger apphost that starts up via JIT instead of direct machine code.
      echo "[package] [warning] AOT publish failed for '$flow' ($RID); trying self-contained fallback (no AOT)…" >&2
      dotnet publish "$project" -c Release -r "$RID" --self-contained true -p:PublishAot=false
      used_fallback=true
      AOT_FALLBACK_FLOWS+=("$flow")
    fi

    pubdir="$(dirname "$project")/bin/Release/net10.0/$RID/publish"
    [[ -f "$pubdir/$bin" ]] || { echo "[error] binary not found at $pubdir/$bin" >&2; exit 1; }
    if [[ "$used_fallback" == true ]]; then
      # Self-contained is NOT a single binary: the apphost ($bin) loads the companion .dll,
      # the *.deps.json/*.runtimeconfig.json and the runtime's native libs, all in the same
      # directory. Copying just the apphost breaks execution ("application to execute does
      # not exist: ...dll") — it needs the whole publish/ directory.
      cp -R "$pubdir/." "$OUT/bin/"
    else
      # Native AOT really is a single, self-contained binary — just that.
      cp "$pubdir/$bin" "$OUT/bin/"
    fi

    # .cmd wrapper (Windows)
    if [[ "$RID" == win-* ]]; then
      cmd="${wrapper%.sh}.cmd"
      cat > "$OUT/$cmd" <<EOF
@echo off
cd /d "%~dp0"
"bin\\$bin" %*
EOF
    fi
  elif [[ "$ENGINE" == "rust" ]]; then
    bin_name="$(rust_bin_for "$flow")"
    bin="$bin_name$WINEXT"

    if ! command -v cargo >/dev/null 2>&1; then
      # shellcheck disable=SC1091
      [[ -f "$HOME/.cargo/env" ]] && source "$HOME/.cargo/env"
    fi
    command -v cargo >/dev/null 2>&1 || { echo "[error] cargo not found — install via https://rustup.rs" >&2; exit 1; }

    echo "[package] compiling (cargo build --release) — ${flow}…"
    ( cd "src/rust" && cargo build --release --bin "$bin_name" )

    pubbin="src/rust/target/release/$bin"
    [[ -f "$pubbin" ]] || { echo "[error] binary not found at $pubbin" >&2; exit 1; }
    cp "$pubbin" "$OUT/bin/"

    # .cmd wrapper — only makes sense if the binary was compiled on a Windows host (cargo
    # itself doesn't cross-compile here, so there's no "win RID on a non-Windows host" case).
    if [[ "$WINEXT" == ".exe" ]]; then
      cmd="${wrapper%.sh}.cmd"
      cat > "$OUT/$cmd" <<EOF
@echo off
cd /d "%~dp0"
"bin\\$bin" %*
EOF
    fi
  elif [[ "$ENGINE" == "go" ]]; then
    bin_name="$(go_bin_for "$flow")"
    bin="$bin_name$WINEXT"

    command -v go >/dev/null 2>&1 || { echo "[error] go not found — install via https://go.dev/dl/" >&2; exit 1; }

    echo "[package] compiling (go build) — ${flow}…"
    ( cd "src/go" && go build -o "bin/$bin" "./$bin_name" )

    pubbin="src/go/bin/$bin"
    [[ -f "$pubbin" ]] || { echo "[error] binary not found at $pubbin" >&2; exit 1; }
    cp "$pubbin" "$OUT/bin/"

    # .cmd wrapper — only makes sense if the binary was compiled on a Windows host (this
    # script doesn't cross-compile GOOS/GOARCH, so there's no "win RID on a non-Windows
    # host" case).
    if [[ "$WINEXT" == ".exe" ]]; then
      cmd="${wrapper%.sh}.cmd"
      cat > "$OUT/$cmd" <<EOF
@echo off
cd /d "%~dp0"
"bin\\$bin" %*
EOF
    fi
  else
    module="$(python_module_for "$flow")"

    echo "[package] copying Python engine — ${flow}…"
    cp -R "src/python/$module" "$OUT/engine/$module"
    find "$OUT/engine/$module" -name "__pycache__" -type d -exec rm -rf {} +

    # .cmd wrapper — always generated (the python engine runs on any OS, unlike the .NET
    # binary which only gets a .cmd when the RID is win-*). Uses "python" (the convention of
    # the official Windows installer), not "python3" (used in the .sh for macOS/Linux).
    cmd="${wrapper%.sh}.cmd"
    cat > "$OUT/$cmd" <<EOF
@echo off
cd /d "%~dp0"
set PYTHONPATH=%~dp0engine;%PYTHONPATH%
python -m $module %*
EOF
  fi

  # Install the engine-owned wrapper only in the assembled package. The repository root
  # deliberately has no run-* entry point.
  cp "$wrapper_src" "$OUT/$wrapper"
  chmod +x "$OUT/$wrapper"

  # IDE adapter (at the path it expects)
  IFS=$'\t' read -r src rel < <(adapter_for "$IDE" "$flow")
  mkdir -p "$OUT/$(dirname "$rel")"
  cp "$src" "$OUT/$rel"
done

# ---- IDE approval config (wrappers run with no per-command prompt) ----
CONFROW=""
case "$IDE" in
  claude)
    # permission allowlist: the agent drives the wrappers without asking for approval on each step
    mkdir -p "$OUT/.claude"
    cat > "$OUT/.claude/settings.json" <<'EOF'
{
  "permissions": {
    "allow": [
      "Bash(./run-development.sh *)",
      "Bash(./run-development.cmd *)",
      "Bash(chmod +x *)"
    ]
  }
}
EOF
    CONFROW="| \`.claude/settings.json\` | allowlist: wrappers run with no approval prompt |
"
    ;;
  copilot)
    # terminal auto-approve in agent mode (VS Code asks for a one-time confirmation
    # to honor auto-approve coming from workspace settings)
    mkdir -p "$OUT/.vscode"
    cat > "$OUT/.vscode/settings.json" <<'EOF'
{
  "chat.tools.terminal.autoApprove": {
    "/^\.\\/(run-development)\\.sh\\b/": true,
    "/^(\\.\\\\)?(run-development)\\.cmd\\b/": true,
    "/^bash +run-development\\.sh\\b/": true,
    "/^chmod \\+x /": true
  }
}
EOF
    CONFROW="| \`.vscode/settings.json\` | terminal auto-approve: wrappers run with no prompt |
"
    ;;
  devin)
    # nothing to generate: the copied workflows already bring auto_execution_mode: 3 (auto-exec)
    ;;
  codex)
    # Codex doesn't read workspace approval config; the instruction goes in START-HERE
    ;;
esac

# ---- per-IDE start instructions ----
DEV_REL="$(adapter_for "$IDE" development | cut -f2)"
case "$IDE" in
  claude)  START="1. Open **this folder** in Claude Code.
2. **Development:** \`/agents\` → **development** and ask *\"Develop: <project goal>\"*. The agent drives \`./run-development.sh\`, one feature at a time, until they all pass.";;
  copilot) START="1. Open **this folder** in VS Code with GitHub Copilot in **agent mode**.
2. **Development:** select the **development** prompt file (\`.github/prompts/development.prompt.md\`) and ask *\"Develop: <project goal>\"*. The agent drives \`./run-development.sh\`, one feature at a time, until they all pass.";;
  devin)   START="1. Open **this folder** as a workspace in Devin Desktop (the workflows are already under \`.devin/workflows/\`).
2. **Development:** invoke \`/development\` and ask *\"Develop: <project goal>\"*. Devin drives \`./run-development.sh\`, one feature at a time, until they all pass.";;
  codex)   START="1. Open **this folder** in Codex. For the wrapper to run without per-command approval, start with \`codex --ask-for-approval never --sandbox workspace-write\` (Codex doesn't read workspace approval config).
2. **Development:** ask *\"Use the custom development agent to develop: <project goal>\"*. The agent at \`.codex/agents/development.toml\` drives \`./run-development.sh\`, one feature at a time, until they all pass.";;
esac

WINROW=""
if { [[ "$ENGINE" == "dotnet" ]] && [[ "$RID" == win-* ]]; } \
  || [[ "$ENGINE" == "python" ]] \
  || { [[ "$ENGINE" == "rust" || "$ENGINE" == "go" ]] && [[ "$WINEXT" == ".exe" ]]; }; then
  WINROW="| \`run-development.cmd\` | execution wrapper on Windows |
"
fi

FALLBACK_NOTE=""
if [[ ${#AOT_FALLBACK_FLOWS[@]} -gt 0 ]]; then
  FALLBACK_NOTE="
**Warning — fallback without Native AOT.** AOT publish failed on this machine for: ${AOT_FALLBACK_FLOWS[*]}.
The binary(ies) for that/those flow(s) were published in *self-contained* mode (the .NET
runtime embedded in the package — the target machine still doesn't need .NET installed),
just larger and starting up via JIT instead of direct native code. To get the real Native
AOT binary, run \`./package.sh\` on a host with the AOT toolchain installed (Xcode Command
Line Tools on macOS, clang + zlib1g-dev on Linux) and on the same OS as the target RID (AOT
doesn't cross-compile).
"
fi

if [[ "$ENGINE" == "dotnet" ]]; then
  TITLE_META="$RID · v$VERSION · IDE: $IDE · engine: dotnet (Native AOT)"
  ENGINE_INTRO="Self-contained package with the development flow as a native binary (no .NET runtime),
plus the skills and the matching IDE adapter."
  ENGINE_ROW="| \`bin/Flows.Development$WINEXT\` | native binary of the development flow |"
elif [[ "$ENGINE" == "rust" ]]; then
  TITLE_META="$HOSTRID · v$VERSION · IDE: $IDE · engine: rust (native)"
  ENGINE_INTRO="Self-contained package with the development flow as a native Rust binary (compiled via
\`cargo build --release\`, no runtime required on the target machine), plus the skills and the
matching IDE adapter. **The binary is native to the host where \`package.sh\` ran** — \`cargo\`
doesn't cross-compile here, so build the package on the same OS/architecture as the target."
  ENGINE_ROW="| \`bin/flows_development$WINEXT\` | native (Rust) binary of the development flow |"
elif [[ "$ENGINE" == "go" ]]; then
  TITLE_META="$HOSTRID · v$VERSION · IDE: $IDE · engine: go (native)"
  ENGINE_INTRO="Self-contained package with the development flow as a native Go binary (compiled via
\`go build\`, no runtime required on the target machine), plus the skills and the matching IDE
adapter. **The binary is native to the host where \`package.sh\` ran** — this script doesn't
cross-compile (GOOS/GOARCH) here, so build the package on the same OS/architecture as the target."
  ENGINE_ROW="| \`bin/flowsdevelopment$WINEXT\` | native (Go) binary of the development flow |"
else
  TITLE_META="python · v$VERSION · IDE: $IDE · engine: python"
  ENGINE_INTRO="Package with the development flow on the Python engine (\`engine/\`, source — no build),
plus the skills and the matching IDE adapter. **Requires \`python3\` (macOS/Linux) or
\`python\` (Windows) in the target machine's PATH** — unlike the \`--engine dotnet\` package,
this one doesn't embed a self-contained binary."
  ENGINE_ROW="| \`engine/\` | Python engine — \`harness_engine/\` + \`flows_development/\` (source, requires python3/python in PATH) |"
fi

cat > "$OUT/START-HERE.md" <<EOF
# Flows — package ($TITLE_META)

$ENGINE_INTRO Development builds the project
feature by feature and saves snapshots to \`last-development.*\` so it doesn't collide with
other flows in the workspace.
$FALLBACK_NOTE
## Getting started

$START

## Quick test (no IDE)

\`\`\`bash
./run-development.sh '{ "type": "text", "value": "start" }'
\`\`\`
The binary should print an \`<input>\`/\`<response>\` block to stdout (or \`stop\`).

## Contents

| Path | What |
|---|---|
$ENGINE_ROW
| \`skills/\` | skills injected at runtime |
| \`scripts/\` | driver usage/correlate — dependency of the cost report (\`skills/session-report\`) |
| \`harness.json\` | harness config: step/cost/time ceilings and docs folder |
| \`run-development.sh\` | execution wrapper |
$WINROW| \`$DEV_REL\` | development adapter for the chosen IDE |
$CONFROW
EOF

if [[ ${#AOT_FALLBACK_FLOWS[@]} -gt 0 ]]; then
  echo "[package] [warning] published with self-contained fallback (no Native AOT) for: ${AOT_FALLBACK_FLOWS[*]} — see START-HERE.md" >&2
fi

echo "[package] done ✓  → $OUT"
echo "[package] contents:"
find "$OUT" -type f | sed "s|^$OUT/|  |" | sort
