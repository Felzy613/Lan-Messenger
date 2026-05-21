#!/usr/bin/env bash
# build_app.sh — Convenience wrapper for local developers.
#
# Calls the canonical packaging pipeline at scripts/macos/package.sh, which
# builds + signs + packages a PKG (primary installer) and ZIP (updater channel).
# Uses ad-hoc signing by default (no Apple Developer cert required).
#
# Usage (from src/macos/):
#   ./scripts/build_app.sh
#   SIGNING_IDENTITY="Developer ID Application: …" ./scripts/build_app.sh
#   SIGNING_IDENTITY="…" NOTARIZE=1 NOTARY_APPLE_ID=… NOTARY_TEAM_ID=… \
#     NOTARY_PASSWORD=… ./scripts/build_app.sh
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

exec "$REPO_ROOT/scripts/macos/package.sh"
