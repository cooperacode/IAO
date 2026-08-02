#!/usr/bin/env bash
# Validates the repository release version and keeps language manifests aligned
# with the single version declared in VERSION.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION_FILE="$DIR/VERSION"
[[ -f "$VERSION_FILE" ]] || { echo "version file not found: $VERSION_FILE" >&2; exit 1; }

VERSION="$(<"$VERSION_FILE")"
SEMVER_RE='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'
[[ "$VERSION" =~ $SEMVER_RE ]] || {
  echo "invalid semantic version in VERSION: '$VERSION'" >&2
  exit 1
}

if [[ $# -gt 1 ]]; then
  echo "usage: bash scripts/check-version.sh [vMAJOR.MINOR.PATCH]" >&2
  exit 1
fi

if [[ $# -eq 1 && "$1" != "v$VERSION" ]]; then
  echo "tag '$1' does not match VERSION '$VERSION' (expected 'v$VERSION')" >&2
  exit 1
fi

cargo_version() {
  awk '
    /^\[package\]$/ { in_package = 1; next }
    /^\[/ { in_package = 0 }
    in_package && /^version = / {
      gsub(/^version = \"|\"$/, "")
      print
      exit
    }
  ' "$1"
}

assert_version() {
  local file="$1" actual="$2"
  if [[ "$actual" != "$VERSION" ]]; then
    echo "version mismatch: $file declares '$actual', expected '$VERSION'" >&2
    exit 1
  fi
}

assert_version "src/python/pyproject.toml" \
  "$(sed -nE 's/^version = \"([^\"]+)\"$/\1/p' "$DIR/src/python/pyproject.toml" | head -n 1)"
assert_version "src/rust/harness_engine/Cargo.toml" \
  "$(cargo_version "$DIR/src/rust/harness_engine/Cargo.toml")"
assert_version "src/rust/flows_development/Cargo.toml" \
  "$(cargo_version "$DIR/src/rust/flows_development/Cargo.toml")"

echo "[version] $VERSION ✓"
