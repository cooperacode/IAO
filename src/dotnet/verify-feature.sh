#!/usr/bin/env bash
# Idempotent per-feature verification for the src/dotnet solution (Flows.Specification target).
# Runs the real build + test pipeline; there is no cheaper per-feature convention yet, so the
# full suite (build + Harness.Engine.Tests, which houses SpecificationEvaluatorTests,
# SpecificationFlowTests, SpecificationPublisherTests and SpecificationToDevelopmentTests) is
# the established verification path.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

./init.sh
dotnet test Harness.Engine.Tests/Harness.Engine.Tests.csproj -c Release

echo "PASS: feature ${1:-all} verified"
