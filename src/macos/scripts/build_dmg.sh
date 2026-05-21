#!/usr/bin/env bash
# build_dmg.sh — Deprecated. DMG is no longer the primary macOS installer.
#
# The release pipeline now ships a PKG (with pre/postinstall scripts) as the
# primary artifact. Use build_pkg.sh instead:
#
#   ./scripts/build_pkg.sh
#
# This wrapper is kept only for backwards compatibility and now delegates to
# build_pkg.sh.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
echo "::warning::build_dmg.sh is deprecated — the macOS release now ships a .pkg. Delegating to build_pkg.sh."
exec "$SCRIPT_DIR/build_pkg.sh" "$@"
