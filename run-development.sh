#!/usr/bin/env bash
# Wrapper de invocação estável do flow de desenvolvimento long-running.
# start → plan → [bearings → smoke → pick → implement → verify → handoff]*
#
#   AOT (recomendado):  dotnet publish src/dotnet/Flows.Development/Flows.Development.csproj -c Release -r <RID>
#   DLL:                dotnet build   src/dotnet/Flows.Development/Flows.Development.csproj -c Release
#
# RIDs: osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE="$DIR/src/dotnet/Flows.Development/bin/Release/net10.0"

# 1) binário nativo (qualquer RID publicado)
for native in "$BASE"/*/publish/Flows.Development; do
  if [[ -x "$native" && ! -d "$native" ]]; then
    exec "$native" "$@"
  fi
done

# 2) fallback: DLL via host dotnet
DLL="$BASE/Flows.Development.dll"
if [[ -f "$DLL" ]]; then
  exec dotnet "$DLL" "$@"
fi

echo "[harness] nenhum artefato encontrado em $BASE" >&2
echo "[harness] rode: dotnet publish src/dotnet/Flows.Development/Flows.Development.csproj -c Release -r osx-arm64" >&2
exit 1
