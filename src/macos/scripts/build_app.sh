#!/usr/bin/env bash
# build_app.sh — Convenience wrapper for local developers.
#
# Calls the canonical packaging pipeline at scripts/macos/package.sh, which
# builds + signs + packages a DMG/ZIP/PKG. By default it skips PKG (faster)
# and uses ad-hoc signing (no Apple Developer cert required).
#
# Usage (from src/macos/):
#   ./scripts/build_app.sh             # ad-hoc, no PKG
#   SKIP_PKG=0 ./scripts/build_app.sh  # also build PKG
#   SIGNING_IDENTITY="Developer ID Application: …" ./scripts/build_app.sh
#
# Artifacts land in <repo-root>/dist/macos/.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

# Read version from the canonical version file so local builds match what
# CI would produce off the same commit.
if [ -z "${VERSION:-}" ]; then
    VERSION=$(jq -r '.version' "$REPO_ROOT/version/macos.json")
fi
export VERSION
export SKIP_PKG="${SKIP_PKG:-1}"

exec "$REPO_ROOT/scripts/macos/package.sh"
