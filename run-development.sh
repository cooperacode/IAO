#!/usr/bin/env bash
# Wrapper de invocação estável do flow de desenvolvimento long-running.
# start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
#
# Sem artefato publicado, builda a DLL sob demanda na primeira chamada (dotnet build).
# Para pacote distribuído sem runtime .NET, publique Native AOT antes:
#   dotnet publish src/dotnet/Flows.Development/Flows.Development.csproj -c Release -r <RID>
#
# RIDs: osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$DIR/src/dotnet/Flows.Development/Flows.Development.csproj"
BASE="$DIR/src/dotnet/Flows.Development/bin/Release/net10.0"
DLL="$BASE/Flows.Development.dll"

# 1) binário nativo (qualquer RID publicado)
for native in "$BASE"/*/publish/Flows.Development; do
  if [[ -x "$native" && ! -d "$native" ]]; then
    exec "$native" "$@"
  fi
done

# 2) DLL via host dotnet — builda sob demanda se ainda não existir
if [[ ! -f "$DLL" ]]; then
  echo "[harness] nenhum artefato encontrado — buildando ($PROJECT)…" >&2
  dotnet build "$PROJECT" -c Release
fi

exec dotnet "$DLL" "$@"
