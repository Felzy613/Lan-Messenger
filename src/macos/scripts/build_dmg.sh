#!/usr/bin/env bash
# build_dmg.sh — Convenience wrapper that builds the same DMG CI ships.
#
# Calls the canonical packaging pipeline at scripts/macos/package.sh. All the
# real logic lives there so a CI-built DMG and a developer-built DMG come from
# the same code path.
#
# Usage (from src/macos/):
#   ./scripts/build_dmg.sh                                  # ad-hoc signed
#   SIGNING_IDENTITY="Developer ID Application: …" \
#     NOTARIZE=1 NOTARY_APPLE_ID=… NOTARY_TEAM_ID=… NOTARY_PASSWORD=… \
#     ./scripts/build_dmg.sh                                # signed + notarized
#
# The resulting DMG lands in <repo-root>/dist/macos/.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

if [ -z "${VERSION:-}" ]; then
    VERSION=$(jq -r '.version' "$REPO_ROOT/version/macos.json")
fi
export VERSION
export SKIP_PKG="${SKIP_PKG:-1}"

exec "$REPO_ROOT/scripts/macos/package.sh"
