#!/usr/bin/env bash
# Idempotent setup for the src/dotnet solution (Flows.Specification target).
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

dotnet restore iao.slnx
dotnet build iao.slnx -c Release
