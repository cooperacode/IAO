#!/usr/bin/env bash
# Manual/teaching wrapper for the hello-world flow (.NET).
# start → ping → pong → stop
# Builds on first use, then hands off to the same argv/inbox transport as any
# other flow — this is the same script a human "pretending to be the LLM"
# calls by hand while learning the IAO protocol.
set -euo pipefail
# NOTE: unlike run-development.sh, this does NOT `cd` into its own directory —
# `.harness/inbox.json` must resolve relative to the caller's workspace (any
# folder the student runs this from), not relative to src/dotnet.
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DLL="$DIR/Flows.HelloWorld/bin/Release/net10.0/Flows.HelloWorld.dll"

if [[ ! -f "$DLL" ]]; then
  echo "[hello-world] building (dotnet build -c Release)…" >&2
  dotnet build "$DIR/Flows.HelloWorld/Flows.HelloWorld.csproj" -c Release >&2
fi

exec dotnet "$DLL" "$@"
