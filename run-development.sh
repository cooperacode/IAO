#!/usr/bin/env bash
# Stable invocation wrapper for the long-running development flow.
# start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
#
# With no published artifact, builds the DLL on demand on the first call (dotnet build).
# For a package distributed without the .NET runtime, publish Native AOT beforehand:
#   dotnet publish src/dotnet/Flows.Development/Flows.Development.csproj -c Release -r <RID>
#
# RIDs: osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$DIR/src/dotnet/Flows.Development/Flows.Development.csproj"
BASE="$DIR/src/dotnet/Flows.Development/bin/Release/net10.0"
DLL="$BASE/Flows.Development.dll"

# 1) native binary (any published RID)
for native in "$BASE"/*/publish/Flows.Development; do
  if [[ -x "$native" && ! -d "$native" ]]; then
    exec "$native" "$@"
  fi
done

# 2) DLL via the dotnet host — builds on demand if it doesn't exist yet
if [[ ! -f "$DLL" ]]; then
  echo "[harness] no artifact found — building ($PROJECT)…" >&2
  dotnet build "$PROJECT" -c Release
fi

exec dotnet "$DLL" "$@"
