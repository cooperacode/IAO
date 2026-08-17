#!/usr/bin/env bash
# Packages the development flow into a self-contained package, choosing the ENGINE
# (--engine) and the operating system (RID, only for --engine dotnet). The package
# bundles the chosen engine, the runtime skills, AND every supported IDE adapter/prompt
# (Claude Code, GitHub Copilot, Devin, Codex) — no driver is chosen at packaging time,
# so the same package works with whichever IDE agent the target machine has. Generates:
#
#   --engine dotnet → dist/flows-<rid>-v<version>/
#     .harness/bin/Flows.Development  # native binary (Native AOT; self-contained if AOT fails)
#     .harness/skills/                # skills injected at runtime
#     .harness/scripts/               # usage/correlate — dependency of the cost report
#     run-development.sh              # root-level development wrapper → .harness/bin
#     .harness/run-development.cmd    # Windows companion wrapper, when applicable
#     <every IDE adapter, at its expected path>
#     <every IDE approval config>     # runs the wrapper with no per-command prompt
#     .harness/START-HERE.md          # how to run the flow, per IDE
#
#   --engine python → dist/flows-python-v<version>/
#     .harness/bin/engine/harness_engine, ...          # Python engine (source, no build)
#     .harness/skills/, .harness/scripts/              # same layout, requires python3/python
#     run-development.sh                              # root-level development wrapper
#     <every IDE adapter/approval config>, .harness/START-HERE.md
#
#   --engine rust → dist/flows-rust-<host-rid>-v<version>/
#     .harness/bin/flows_development  # native binary (cargo build --release --bin flows_development)
#     .harness/skills/, .harness/scripts/              # same layout as dotnet
#     run-development.sh                              # root-level development wrapper
#     <every IDE adapter/approval config>, .harness/START-HERE.md
#
#   --engine go → dist/flows-go-<host-rid>-v<version>/
#     .harness/bin/flowsdevelopment   # native binary (go build ./flowsdevelopment)
#     .harness/skills/, .harness/scripts/              # same layout as rust
#     run-development.sh                              # root-level development wrapper
#     <every IDE adapter/approval config>, .harness/START-HERE.md
#
# Optional, any engine (--with-gui):
#     .harness/gui/                   # local GUI: drives Claude/Codex in background over the
#                                      # wrapper(s) above and shows live progress + a
#                                      # specs/sources manager. stdlib Python 3, no extra
#                                      # dependency. Purely additive — doesn't change anything
#                                      # else in the package.
#
# Usage:
#   ./package.sh --engine <dotnet|python|rust|go> [--os <rid>] [--version <v>] [--with-gui]
#   ./package.sh                     # interactive menu (engine, and OS for dotnet)
#
# --os/--rid only applies to --engine dotnet (Native AOT compiles per OS). The python
# engine runs the same on any OS with the interpreter in PATH — there's no RID for it. The
# rust and go engines also ignore --os: neither `cargo build --release` nor `go build`
# cross-compile here, so the binary comes out native to the host this script ran on — the
# <host-rid> in the package name is auto-detected (uname -s/-m), not selectable.
# RIDs (--engine dotnet only): osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64
#
# --ide is no longer needed: every package now bundles the adapter and approval config for
# every supported IDE. The flag is still accepted for backward compatibility and ignored
# (with a warning) so older invocations keep working.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

ENGINES=(dotnet python rust go)
RIDS=(osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64)
# Every package bundles all of these — this list is now for iteration only, never selection.
IDES=(claude copilot devin codex kimi)
# FLOWS is derived once ENGINE is known (see below, right after engine validation) —
# `specification` only exists on the dotnet engine today (no python/rust/go port), so it's
# only added to the list when packaging that engine.

ENGINE=""
RID=""
WITH_GUI=false
VERSION_FILE="$DIR/VERSION"
[[ -f "$VERSION_FILE" ]] || { echo "version file not found: $VERSION_FILE" >&2; exit 1; }
REPOSITORY_VERSION="$(<"$VERSION_FILE")"
VERSION="$REPOSITORY_VERSION"

# ---- visual identity ----------------------------------------------------------------
# Colors degrade to empty strings when stdout isn't a TTY or the terminal has no color
# support (CI logs, redirected output) — the script stays perfectly readable either way.
setup_colors() {
  C_RESET=""; C_BOLD=""; C_DIM=""; C_CYAN=""; C_GREEN=""; C_YELLOW=""
  if [[ -t 1 ]] && command -v tput >/dev/null 2>&1; then
    local ncolors
    ncolors="$(tput colors 2>/dev/null || echo 0)"
    if [[ "$ncolors" -ge 8 ]]; then
      C_RESET="$(tput sgr0)"
      C_BOLD="$(tput bold)"
      C_DIM="$(tput dim 2>/dev/null || true)"
      C_CYAN="$(tput setaf 6)"
      C_GREEN="$(tput setaf 2)"
      C_YELLOW="$(tput setaf 3)"
    fi
  fi
}
setup_colors

print_logo() {
  printf '%s%s' "$C_CYAN" "$C_BOLD"
  cat <<'LOGO'

  █████   ███    ███
    █    █   █  █   █
    █    █████  █   █
    █    █   █  █   █
  █████  █   █   ███
LOGO
  printf '%s' "$C_RESET"
  printf '  %sInverted Agentic Orchestration%s %s·%s %spackage builder%s\n' \
    "$C_BOLD" "$C_RESET" "$C_DIM" "$C_RESET" "$C_DIM" "$C_RESET"
  echo
}

print_logo

usage() {
  echo "usage: ./package.sh --engine <dotnet|python|rust|go> [--os <rid>] [--version <v>] [--with-gui]"
  echo "       ./package.sh                     # interactive menu"
  echo
  echo "engines: ${ENGINES[*]}"
  echo "RIDs (--engine dotnet only): ${RIDS[*]}"
  echo "--with-gui: also bundle .harness/gui/ (optional local control panel, any engine/IDE)"
  echo
  echo "IDE adapters (${IDES[*]}) are always bundled — no --ide selection needed anymore."
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
    --ide)
      echo "[package] [warning] --ide '${2:-}' ignored: every IDE adapter is bundled now, nothing to select." >&2
      shift 2;;
    --version|-v) VERSION="${2:-}"; shift 2;;
    --with-gui) WITH_GUI=true; shift;;
    -h|--help)  usage; exit 0;;
    *) echo "unknown argument: $1" >&2; usage; exit 1;;
  esac
done

contains() { local x; for x in "${@:2}"; do [[ "$x" == "$1" ]] && return 0; done; return 1; }

# ---- interactive menu helpers ----
# Custom numbered menus (instead of bash's plain `select`) for a clearer, branded prompt.
# Decorative output goes to stderr; only the final selected value is written to stdout, so
# these can be used directly in a command substitution (VAR="$(ask_engine)").
engine_desc() { case "$1" in
  dotnet) echo ".NET Native AOT — single self-contained binary";;
  python) echo "Python engine — source, requires python3/python in PATH";;
  rust)   echo "Rust native binary — cargo build --release";;
  go)     echo "Go native binary — go build";;
esac; }
rid_desc() { case "$1" in
  osx-arm64)   echo "macOS · Apple Silicon";;
  osx-x64)     echo "macOS · Intel";;
  linux-x64)   echo "Linux · x64";;
  linux-arm64) echo "Linux · ARM64";;
  win-x64)     echo "Windows · x64";;
esac; }

print_menu_header() {
  printf '%s%s%s\n' "$C_BOLD" "$1" "$C_RESET" >&2
}

ask_engine() {
  print_menu_header "Select the harness engine:" >&2
  local i=1 e
  for e in "${ENGINES[@]}"; do
    printf '  %s%d)%s %s%-8s%s %s%s%s\n' \
      "$C_CYAN" "$i" "$C_RESET" "$C_BOLD" "$e" "$C_RESET" "$C_DIM" "$(engine_desc "$e")" "$C_RESET" >&2
    i=$((i + 1))
  done
  local choice
  while true; do
    printf '%sengine%s [1-%d]: ' "$C_BOLD" "$C_RESET" "${#ENGINES[@]}" >&2
    read -r choice
    if [[ "$choice" =~ ^[0-9]+$ ]] && (( choice >= 1 && choice <= ${#ENGINES[@]} )); then
      printf '%s\n' "${ENGINES[$((choice - 1))]}"
      return 0
    fi
    printf '  %sinvalid choice%s — enter a number between 1 and %d\n' "$C_YELLOW" "$C_RESET" "${#ENGINES[@]}" >&2
  done
}

ask_rid() {
  print_menu_header "Select the operating system (RID):" >&2
  local i=1 r
  for r in "${RIDS[@]}"; do
    printf '  %s%d)%s %s%-11s%s %s%s%s\n' \
      "$C_CYAN" "$i" "$C_RESET" "$C_BOLD" "$r" "$C_RESET" "$C_DIM" "$(rid_desc "$r")" "$C_RESET" >&2
    i=$((i + 1))
  done
  local choice
  while true; do
    printf '%sos%s [1-%d]: ' "$C_BOLD" "$C_RESET" "${#RIDS[@]}" >&2
    read -r choice
    if [[ "$choice" =~ ^[0-9]+$ ]] && (( choice >= 1 && choice <= ${#RIDS[@]} )); then
      printf '%s\n' "${RIDS[$((choice - 1))]}"
      return 0
    fi
    printf '  %sinvalid choice%s — enter a number between 1 and %d\n' "$C_YELLOW" "$C_RESET" "${#RIDS[@]}" >&2
  done
}

# ---- per-flow metadata ----
project_for() { case "$1" in
  development)   echo "src/dotnet/Flows.Development/Flows.Development.csproj";;
  specification) echo "src/dotnet/Flows.Specification/Flows.Specification.csproj";;
esac; }
assembly_for() { case "$1" in
  development)   echo "Flows.Development";;
  specification) echo "Flows.Specification";;
esac; }
wrapper_for() { case "$1" in
  development)   echo "run-development.sh";;
  specification) echo "run-specification.sh";;
esac; }
# Each engine owns the wrapper installed at the package root. Keeping these templates
# beside their implementation avoids executable entry points at the repository root.
wrapper_source_for() {
  local engine="$1" flow="$2"
  case "$engine:$flow" in
    dotnet:development)   echo "src/dotnet/run-development.sh";;
    dotnet:specification) echo "src/dotnet/run-specification.sh";;
    python:development)   echo "src/python/run-development-py.sh";;
    python:specification) echo "src/python/run-specification-py.sh";;
    rust:development)     echo "src/rust/run-development-rs.sh";;
    rust:specification)   echo "src/rust/run-specification-rs.sh";;
    go:development)       echo "src/go/run-development-go.sh";;
    go:specification)     echo "src/go/run-specification-go.sh";;
  esac
}
# --engine python only: name of the package under src/python/ that implements the flow.
python_module_for() { case "$1" in
  development) echo "flows_development";;
  specification) echo "flows_specification";;
esac; }
# --engine rust only: name of the bin crate's binary under src/rust/ that implements the flow.
rust_bin_for() { case "$1" in
  development) echo "flows_development";;
  specification) echo "flows_specification";;
esac; }
# --engine go only: name of the main package's binary under src/go/ that implements the flow.
go_bin_for() { case "$1" in
  development) echo "flowsdevelopment";;
  specification) echo "flowsspecification";;
esac; }
# adapter per IDE+flow → "SRC<TAB>REL" (REL = path expected by the IDE inside the package)
adapter_for() { case "$1:$2" in
  claude:development)    printf '%s\t%s\n' ".claude/agents/development.agent.md"      ".claude/agents/development.agent.md";;
  claude:specification)  printf '%s\t%s\n' ".claude/agents/specification.agent.md"    ".claude/agents/specification.agent.md";;
  copilot:development)   printf '%s\t%s\n' ".github/prompts/development.prompt.md"    ".github/prompts/development.prompt.md";;
  copilot:specification) printf '%s\t%s\n' ".github/prompts/specification.prompt.md"  ".github/prompts/specification.prompt.md";;
  devin:development)     printf '%s\t%s\n' ".devin/workflows/development.md"          ".devin/workflows/development.md";;
  devin:specification)   printf '%s\t%s\n' ".devin/workflows/specification.md"        ".devin/workflows/specification.md";;
  codex:development)     printf '%s\t%s\n' ".codex/agents/development.toml"           ".codex/agents/development.toml";;
  codex:specification)   printf '%s\t%s\n' ".codex/agents/specification.toml"         ".codex/agents/specification.toml";;
  kimi:development)      printf '%s\t%s\n' ".kimi/agents/development.md"              ".kimi/agents/development.md";;
  kimi:specification)    printf '%s\t%s\n' ".kimi/agents/specification.md"            ".kimi/agents/specification.md";;
esac; }
# Human-readable label per IDE, used in menus/docs.
ide_label() { case "$1" in
  claude)  echo "Claude Code";;
  copilot) echo "GitHub Copilot";;
  devin)   echo "Devin";;
  codex)   echo "Codex";;
  kimi)    echo "Kimi Code CLI";;
esac; }

# ---- interactive selection when missing ----
[[ -z "$ENGINE" ]] && ENGINE="$(ask_engine)"
# RID only exists for the dotnet engine (Native AOT compiles per OS); the python engine
# runs the same on any OS with the interpreter in PATH.
if [[ "$ENGINE" == "dotnet" && -z "$RID" ]]; then
  RID="$(ask_rid)"
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
if [[ "$WITH_GUI" == true && ! -d ".harness/gui" ]]; then
  echo "--with-gui requested but .harness/gui/ does not exist in this checkout" >&2
  exit 1
fi

# Every engine now ships both flows; the adapters are shared while each engine supplies its
# own specification implementation and wrapper.
FLOWS=(development specification)

[[ -n "$VERSION" ]] || { echo "empty version" >&2; exit 1; }
SEMVER_RE='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'
[[ "$VERSION" =~ $SEMVER_RE ]] || {
  echo "invalid semantic version: '$VERSION' (expected MAJOR.MINOR.PATCH)" >&2
  exit 1
}
bash "$DIR/scripts/check-version.sh"
if [[ "$VERSION" != "$REPOSITORY_VERSION" ]]; then
  echo "[package] [warning] using --version '$VERSION' instead of repository VERSION '$REPOSITORY_VERSION'" >&2
fi

bash "$DIR/.harness/scripts/check-development-contracts.sh"

# every flow's adapter must exist for every bundled IDE
for flow in "${FLOWS[@]}"; do
  wrapper_src="$(wrapper_source_for "$ENGINE" "$flow")"
  [[ -f "$wrapper_src" ]] || {
    echo "wrapper not found: $wrapper_src (engine=$ENGINE, flow=$flow)" >&2
    exit 1
  }
  for ide in "${IDES[@]}"; do
    IFS=$'\t' read -r src _rel < <(adapter_for "$ide" "$flow")
    [[ -f "$src" ]] || { echo "adapter not found: $src (ide=$ide, flow=$flow)" >&2; exit 1; }
  done
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
mkdir -p "$OUT/.harness"
if [[ "$WITH_GUI" == true ]]; then
  # Optional local control panel: a stdlib-Python orchestrator that drives Claude/Codex in
  # background over the wrapper(s) this package already installs, plus a specs/sources
  # manager for the specification flow.
  echo "[package] including optional GUI orchestrator (.harness/gui/)…"
  mkdir -p "$OUT/.harness/gui"
  cp .harness/gui/*.py "$OUT/.harness/gui/"
  cp .harness/gui/index.html "$OUT/.harness/gui/index.html"
  cp .harness/gui/run.sh "$OUT/.harness/gui/run.sh"
  chmod +x "$OUT/.harness/gui/run.sh"
fi
[[ "$ENGINE" == "dotnet" || "$ENGINE" == "rust" || "$ENGINE" == "go" ]] && mkdir -p "$OUT/.harness/bin"
cp -R .harness/skills "$OUT/.harness/skills"
find "$OUT/.harness/skills" -name "__pycache__" -type d -prune -exec rm -rf {} +
cp harness.json "$OUT/harness.json"   # harness variable config (ceilings, docs)
cp harness.schema.json "$OUT/harness.schema.json"   # harness.json's own "$schema" points here
if contains specification "${FLOWS[@]}"; then
  # Specification publishes its bundle to specs/active/ — point the packaged Development's
  # docsFolder there by default so a downstream user who runs both flows gets the handoff
  # for free, without extra configuration.
  sed -i.bak 's/"docsFolder": *"specs"/"docsFolder": "specs\/active"/' "$OUT/harness.json"
  rm -f "$OUT/harness.json.bak"
fi
printf '%s\n' "$VERSION" > "$OUT/VERSION"

# .harness/scripts/ — dependency of .harness/skills/session-report/generate_report.py.
# Keep provider usage scripts inside the harness container so the package root only exposes
# the stable config and invocation wrapper.
mkdir -p "$OUT/.harness/scripts"
cp .harness/scripts/*.py "$OUT/.harness/scripts/"

# ---- python engine: the engine is source, not a build — copy harness_engine (shared
# across flows) just once, outside the per-flow loop (the same pattern as .harness/skills
# and .harness/scripts above) ----
if [[ "$ENGINE" == "python" ]]; then
  mkdir -p "$OUT/.harness/bin/engine"
  cp -R "src/python/harness_engine" "$OUT/.harness/bin/engine/harness_engine"
  find "$OUT/.harness/bin/engine/harness_engine" -name "__pycache__" -type d -exec rm -rf {} +
fi

# ---- per flow: engine (.NET build or copy of the Python source), wrapper and every IDE adapter ----
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
      cp -R "$pubdir/." "$OUT/.harness/bin/"
    else
      # Native AOT really is a single, self-contained binary — just that.
      cp "$pubdir/$bin" "$OUT/.harness/bin/"
    fi

    # .cmd wrapper (Windows)
    if [[ "$RID" == win-* ]]; then
      cmd="${wrapper%.sh}.cmd"
      cat > "$OUT/.harness/$cmd" <<EOF
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
    cp "$pubbin" "$OUT/.harness/bin/"

    # .cmd wrapper — only makes sense if the binary was compiled on a Windows host (cargo
    # itself doesn't cross-compile here, so there's no "win RID on a non-Windows host" case).
    if [[ "$WINEXT" == ".exe" ]]; then
      cmd="${wrapper%.sh}.cmd"
      cat > "$OUT/.harness/$cmd" <<EOF
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
    cp "$pubbin" "$OUT/.harness/bin/"

    # .cmd wrapper — only makes sense if the binary was compiled on a Windows host (this
    # script doesn't cross-compile GOOS/GOARCH, so there's no "win RID on a non-Windows
    # host" case).
    if [[ "$WINEXT" == ".exe" ]]; then
      cmd="${wrapper%.sh}.cmd"
      cat > "$OUT/.harness/$cmd" <<EOF
@echo off
cd /d "%~dp0"
"bin\\$bin" %*
EOF
    fi
  else
    module="$(python_module_for "$flow")"

    echo "[package] copying Python engine — ${flow}…"
    cp -R "src/python/$module" "$OUT/.harness/bin/engine/$module"
    find "$OUT/.harness/bin/engine/$module" -name "__pycache__" -type d -exec rm -rf {} +

    # .cmd wrapper — always generated (the python engine runs on any OS, unlike the .NET
    # binary which only gets a .cmd when the RID is win-*). Uses "python" (the convention of
    # the official Windows installer), not "python3" (used in the .sh for macOS/Linux).
    cmd="${wrapper%.sh}.cmd"
    cat > "$OUT/.harness/$cmd" <<EOF
@echo off
cd /d "%~dp0"
set PYTHONPATH=%~dp0bin\\engine;%PYTHONPATH%
python -m $module %*
EOF
  fi

  # Install the engine-owned wrapper only in the assembled package. The repository root
  # deliberately has no run-* entry point.
  cp "$wrapper_src" "$OUT/$wrapper"
  chmod +x "$OUT/$wrapper"

  # Every IDE adapter (at the path each IDE expects) — the package no longer picks one.
  for ide in "${IDES[@]}"; do
    IFS=$'\t' read -r src rel < <(adapter_for "$ide" "$flow")
    mkdir -p "$OUT/$(dirname "$rel")"
    cp "$src" "$OUT/$rel"
  done
done

# ---- IDE approval config, for every bundled IDE (wrappers run with no per-command prompt) ----
# Wrapper base names actually included in this package (one per FLOWS entry) — feeds every
# block below so a multi-flow dotnet package (development + specification) gets every
# wrapper pre-approved, not just the first one.
WRAPPER_BASES=()
for _flow in "${FLOWS[@]}"; do
  _w="$(wrapper_for "$_flow")"
  WRAPPER_BASES+=("${_w%.sh}")
done

CONFROWS=""
for ide in "${IDES[@]}"; do
  case "$ide" in
    claude)
      # permission allowlist: the agent drives the wrappers without asking for approval on each step
      mkdir -p "$OUT/.claude"
      ALLOW_VALUES=()
      for _base in "${WRAPPER_BASES[@]}"; do
        ALLOW_VALUES+=("Bash(./$_base.sh *)" "Bash(.harness/$_base.cmd *)")
      done
      ALLOW_VALUES+=("Bash(chmod +x *)")
      {
        echo '{'
        echo '  "permissions": {'
        echo '    "allow": ['
        _last=$((${#ALLOW_VALUES[@]} - 1))
        for _i in "${!ALLOW_VALUES[@]}"; do
          if [[ $_i -eq $_last ]]; then
            printf '      "%s"\n' "${ALLOW_VALUES[$_i]}"
          else
            printf '      "%s",\n' "${ALLOW_VALUES[$_i]}"
          fi
        done
        echo '    ]'
        echo '  }'
        echo '}'
      } > "$OUT/.claude/settings.json"
      CONFROWS="$CONFROWS| \`.claude/settings.json\` | Claude Code: allowlist, wrappers run with no approval prompt |
"
      ;;
    copilot)
      # terminal auto-approve in agent mode (VS Code asks for a one-time confirmation
      # to honor auto-approve coming from workspace settings). Written with a placeholder
      # token instead of direct variable interpolation so the heredoc can stay single-quoted
      # (byte-literal, no bash backslash processing that would corrupt the regex escaping).
      mkdir -p "$OUT/.vscode"
      cat > "$OUT/.vscode/settings.json" <<'EOF'
{
  "chat.tools.terminal.autoApprove": {
    "/^\.\\/(__WRAPPER_ALT__)\\.sh\\b/": true,
    "/^\\.harness[\\\\/](__WRAPPER_ALT__)\\.cmd\\b/": true,
    "/^bash +(__WRAPPER_ALT__)\\.sh\\b/": true,
    "/^chmod \\+x /": true
  }
}
EOF
      WRAPPER_ALT="$(IFS='|'; echo "${WRAPPER_BASES[*]}")"
      sed -i.bak "s#__WRAPPER_ALT__#$WRAPPER_ALT#g" "$OUT/.vscode/settings.json"
      rm -f "$OUT/.vscode/settings.json.bak"
      CONFROWS="$CONFROWS| \`.vscode/settings.json\` | GitHub Copilot: terminal auto-approve, wrappers run with no prompt |
"
      ;;
    devin)
      # nothing to generate: the copied workflows already bring auto_execution_mode: 3 (auto-exec)
      ;;
    codex)
      # Codex doesn't read workspace approval config; the instruction goes in START-HERE
      ;;
    kimi)
      # No known workspace-level trust/approval config file for Kimi Code CLI; non-interactive
      # `-p` mode has no approval channel to begin with (see .harness/gui/drivers.py). The
      # launch instruction goes in START-HERE, like Codex.
      ;;
  esac
done

# ---- start instructions, per bundled IDE ----
HAS_SPEC=false
contains specification "${FLOWS[@]}" && HAS_SPEC=true

DEV_REL_claude="$(adapter_for claude development | cut -f2)"
DEV_REL_copilot="$(adapter_for copilot development | cut -f2)"
DEV_REL_devin="$(adapter_for devin development | cut -f2)"
DEV_REL_codex="$(adapter_for codex development | cut -f2)"
DEV_REL_kimi="$(adapter_for kimi development | cut -f2)"
SPEC_REL_claude=""; SPEC_REL_copilot=""; SPEC_REL_devin=""; SPEC_REL_codex=""; SPEC_REL_kimi=""
if $HAS_SPEC; then
  SPEC_REL_claude="$(adapter_for claude specification | cut -f2)"
  SPEC_REL_copilot="$(adapter_for copilot specification | cut -f2)"
  SPEC_REL_devin="$(adapter_for devin specification | cut -f2)"
  SPEC_REL_codex="$(adapter_for codex specification | cut -f2)"
  SPEC_REL_kimi="$(adapter_for kimi specification | cut -f2)"
fi

START="Pick whichever IDE agent is on this machine — every adapter below is already in the package, no rebuild needed to switch.

### Claude Code
1. Open **this folder** in Claude Code.
2. **Development:** \`/agents\` → **development** and ask *\"Develop: <project goal>\"*. The agent drives \`./run-development.sh\`, one feature at a time, until they all pass."
$HAS_SPEC && START="$START
3. **Specification:** \`/agents\` → **specification** and ask it to frame your idea (point it at a sources folder with product docs/transcripts/notes if you have one). The agent drives \`./run-specification.sh\` from idea through publish (\`specs/active/\`), which Development can then read as its brief."

START="$START

### GitHub Copilot
1. Open **this folder** in VS Code with GitHub Copilot in **agent mode**.
2. **Development:** select the **development** prompt file (\`.github/prompts/development.prompt.md\`) and ask *\"Develop: <project goal>\"*. The agent drives \`./run-development.sh\`, one feature at a time, until they all pass."
$HAS_SPEC && START="$START
3. **Specification:** select the **specification** prompt file (\`.github/prompts/specification.prompt.md\`) and ask it to frame your idea. The agent drives \`./run-specification.sh\` from idea through publish (\`specs/active/\`), which Development can then read as its brief."

START="$START

### Devin
1. Open **this folder** as a workspace in Devin Desktop (the workflows are already under \`.devin/workflows/\`).
2. **Development:** invoke \`/development\` and ask *\"Develop: <project goal>\"*. Devin drives \`./run-development.sh\`, one feature at a time, until they all pass."
$HAS_SPEC && START="$START
3. **Specification:** invoke \`/specification\` and ask it to frame your idea. Devin drives \`./run-specification.sh\` from idea through publish (\`specs/active/\`), which Development can then read as its brief."

START="$START

### Codex
1. Open **this folder** in Codex. For the wrapper to run without per-command approval, start with \`codex --ask-for-approval never --sandbox workspace-write\` (Codex doesn't read workspace approval config).
2. **Development:** ask *\"Use the custom development agent to develop: <project goal>\"*. The agent at \`.codex/agents/development.toml\` drives \`./run-development.sh\`, one feature at a time, until they all pass."
$HAS_SPEC && START="$START
3. **Specification:** ask *\"Use the custom specification agent to frame: <your idea>\"*. The agent at \`.codex/agents/specification.toml\` drives \`./run-specification.sh\` from idea through publish (\`specs/active/\`), which Development can then read as its brief."

START="$START

### Kimi Code CLI
1. Open **this folder** in a terminal (\`kimi\` reads the workspace from the current directory). Launch each run with \`--agent-file\` pointing at the adapter below — non-interactive \`-p\` mode needs no extra approval flag.
2. **Development:** \`kimi -p \"Develop: <project goal>\" --agent-file .kimi/agents/development.md\`. The agent drives \`./run-development.sh\`, one feature at a time, until they all pass."
$HAS_SPEC && START="$START
3. **Specification:** \`kimi -p \"Frame: <your idea>\" --agent-file .kimi/agents/specification.md\`. The agent drives \`./run-specification.sh\` from idea through publish (\`specs/active/\`), which Development can then read as its brief."

WINROW=""
if { [[ "$ENGINE" == "dotnet" ]] && [[ "$RID" == win-* ]]; } \
  || [[ "$ENGINE" == "python" ]] \
  || { [[ "$ENGINE" == "rust" || "$ENGINE" == "go" ]] && [[ "$WINEXT" == ".exe" ]]; }; then
  for _flow in "${FLOWS[@]}"; do
    _w="$(wrapper_for "$_flow")"
    WINROW="$WINROW| \`.harness/${_w%.sh}.cmd\` | execution wrapper on Windows ($_flow) |
"
  done
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
  TITLE_META="$RID · v$VERSION · engine: dotnet (Native AOT)"
  if $HAS_SPEC; then
    ENGINE_INTRO="Self-contained package with the development and specification flows as native
binaries (no .NET runtime), plus the skills and every supported IDE adapter."
    ENGINE_ROW="| \`.harness/bin/Flows.Development$WINEXT\` | native binary of the development flow |
| \`.harness/bin/Flows.Specification$WINEXT\` | native binary of the specification flow |"
  else
    ENGINE_INTRO="Self-contained package with the development flow as a native binary (no .NET runtime),
plus the skills and every supported IDE adapter."
    ENGINE_ROW="| \`.harness/bin/Flows.Development$WINEXT\` | native binary of the development flow |"
  fi
elif [[ "$ENGINE" == "rust" ]]; then
  TITLE_META="$HOSTRID · v$VERSION · engine: rust (native)"
  ENGINE_INTRO="Self-contained package with the development flow as a native Rust binary (compiled via
\`cargo build --release\`, no runtime required on the target machine), plus the skills and
every supported IDE adapter. **The binary is native to the host where \`package.sh\` ran** —
\`cargo\` doesn't cross-compile here, so build the package on the same OS/architecture as the target."
  ENGINE_ROW="| \`.harness/bin/flows_development$WINEXT\` | native (Rust) binary of the development flow |"
elif [[ "$ENGINE" == "go" ]]; then
  TITLE_META="$HOSTRID · v$VERSION · engine: go (native)"
  ENGINE_INTRO="Self-contained package with the development flow as a native Go binary (compiled via
\`go build\`, no runtime required on the target machine), plus the skills and every supported
IDE adapter. **The binary is native to the host where \`package.sh\` ran** — this script doesn't
cross-compile (GOOS/GOARCH) here, so build the package on the same OS/architecture as the target."
  ENGINE_ROW="| \`.harness/bin/flowsdevelopment$WINEXT\` | native (Go) binary of the development flow |"
else
  TITLE_META="python · v$VERSION · engine: python"
  ENGINE_INTRO="Package with the development flow on the Python engine (\`.harness/bin/engine/\`, source — no build),
plus the skills and every supported IDE adapter. **Requires \`python3\` (macOS/Linux) or
\`python\` (Windows) in the target machine's PATH** — unlike the \`--engine dotnet\` package,
this one doesn't embed a self-contained binary."
  ENGINE_ROW="| \`.harness/bin/engine/\` | Python engine — \`harness_engine/\` + \`flows_development/\` (source, requires python3/python in PATH) |"
fi

SPEC_BLURB=""
QUICK_TEST_SPEC=""
WRAPPER_ROW="| \`run-development.sh\` | execution wrapper (development) |"
ADAPTER_ROWS="| \`$DEV_REL_claude\` | development adapter — Claude Code |
| \`$DEV_REL_copilot\` | development adapter — GitHub Copilot |
| \`$DEV_REL_devin\` | development adapter — Devin |
| \`$DEV_REL_codex\` | development adapter — Codex |
| \`$DEV_REL_kimi\` | development adapter — Kimi Code CLI |"
if $HAS_SPEC; then
  SPEC_BLURB=" Specification takes an idea (plus an optional sources folder of product
docs/transcripts/notes) through PRD/SRS/SDD/readiness/approval and publishes to
\`specs/active/\`, which Development can then read as its brief — snapshots go to
\`last-specification.*\`, so the two flows never collide."
  QUICK_TEST_SPEC="

\`\`\`bash
./run-specification.sh '{ \"type\": \"text\", \"value\": \"start\" }'
\`\`\`"
  WRAPPER_ROW="| \`run-development.sh\` | execution wrapper (development) |
| \`run-specification.sh\` | execution wrapper (specification) |"
  ADAPTER_ROWS="$ADAPTER_ROWS
| \`$SPEC_REL_claude\` | specification adapter — Claude Code |
| \`$SPEC_REL_copilot\` | specification adapter — GitHub Copilot |
| \`$SPEC_REL_devin\` | specification adapter — Devin |
| \`$SPEC_REL_codex\` | specification adapter — Codex |
| \`$SPEC_REL_kimi\` | specification adapter — Kimi Code CLI |"
fi

GUI_ROW=""
GUI_SECTION=""
if [[ "$WITH_GUI" == true ]]; then
  GUI_ROW="| \`.harness/gui/\` | optional local GUI (control panel + monitor + specs/sources manager) |
"
  # Built as a plain top-level if/else (not a "$HAS_SPEC && …" idiom nested inside a $(...)
  # substitution) — under `set -e`, a failing command substitution embedded in an assignment
  # aborts the script immediately, and $HAS_SPEC expands to the literal command `false` when
  # unset, which is exactly the failure this sidesteps.
  GUI_SPEC_NOTE=""
  $HAS_SPEC && GUI_SPEC_NOTE=" It also manages \`specs/sources/\` for the specification flow directly from the browser."
  DEV_WRAPPER="$(wrapper_for development)"
  GUI_SECTION="
## Optional GUI

Prefer a graphical control panel over the IDE agent? Run \`./.harness/gui/run.sh\` (stdlib
Python 3, no extra dependency) and open **http://127.0.0.1:8787**. Pick the driver (Claude or
Codex — whichever CLI is on this machine's PATH) and the flow, confirm the autonomy warning
(it writes files and can commit without review, same as driving it by hand), and click
**Start**. It runs the driver in the background over the same \`$DEV_WRAPPER\`
you'd invoke manually, and shows live progress with backlog/trace panels right in the
browser.$GUI_SPEC_NOTE
"
fi

cat > "$OUT/.harness/START-HERE.md" <<EOF
# Flows — package ($TITLE_META)

$ENGINE_INTRO Development builds the project
feature by feature and saves snapshots to \`last-development.*\` so it doesn't collide with
other flows in the workspace.$SPEC_BLURB
$FALLBACK_NOTE
## Getting started

$START
$GUI_SECTION
## Quick test (no IDE)

\`\`\`bash
./run-development.sh '{ "type": "text", "value": "start" }'
\`\`\`
The binary should print an \`<input>\`/\`<response>\` block to stdout (or \`stop\`).$QUICK_TEST_SPEC

## Contents

| Path | What |
|---|---|
$ENGINE_ROW
| \`.harness/skills/\` | skills injected at runtime |
| \`.harness/scripts/\` | driver usage/correlate — dependency of the cost report (\`.harness/skills/session-report\`) |
| \`harness.json\` | harness config: step/cost/time ceilings and docs folder |
| \`harness.schema.json\` | editor validation/autocomplete for \`harness.json\` (ignored by the harness itself) |
$WRAPPER_ROW
$WINROW$ADAPTER_ROWS
$GUI_ROW$CONFROWS
EOF

if [[ ${#AOT_FALLBACK_FLOWS[@]} -gt 0 ]]; then
  echo "[package] [warning] published with self-contained fallback (no Native AOT) for: ${AOT_FALLBACK_FLOWS[*]} — see .harness/START-HERE.md" >&2
fi

echo "${C_GREEN}[package] done ✓${C_RESET}  → $OUT"
echo "[package] contents:"
find "$OUT" -type f | sed "s|^$OUT/|  |" | sort
