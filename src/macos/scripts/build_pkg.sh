#!/usr/bin/env bash
# build_pkg.sh — Convenience wrapper that builds the same PKG CI ships.
#
# Calls the canonical packaging pipeline at scripts/macos/package.sh. All the
# real logic lives there so a CI-built PKG and a developer-built PKG come from
# the same code path.
#
# Usage (from src/macos/):
#   ./scripts/build_pkg.sh                                  # ad-hoc signed
#   SIGNING_IDENTITY="Developer ID Application: …" \
#     INSTALLER_SIGNING_IDENTITY="Developer ID Installer: …" \
#     NOTARIZE=1 NOTARY_APPLE_ID=… NOTARY_TEAM_ID=… NOTARY_PASSWORD=… \
#     ./scripts/build_pkg.sh                                # signed + notarized
#
# The resulting PKG (and ZIP updater) land in <repo-root>/dist/macos/.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

if [ -z "${VERSION:-}" ]; then
    VERSION=$(jq -r '.version' "$REPO_ROOT/version/macos.json")
fi
export VERSION

exec "$REPO_ROOT/scripts/macos/package.sh"
