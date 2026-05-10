#!/bin/bash
# build_app.sh — Build LAN Messenger.app and copy it to releases/macos/.
#
# Usage (run from src/macos/):
#   ./scripts/build_app.sh
#
# Prerequisites:
#   - Xcode installed
#   - xcodegen installed (brew install xcodegen) — only needed if .xcodeproj is missing
#
# The script produces:
#   <repo-root>/releases/macos/LAN Messenger.app   (or LanMessenger.app)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$PROJECT_DIR/../.." && pwd)"
RELEASES_DIR="$REPO_ROOT/releases/macos"
BUILD_DIR="$PROJECT_DIR/build"
XCODEPROJ="$PROJECT_DIR/LanMessenger.xcodeproj"

# ── 0. Generate project if needed ────────────────────────────────────────────
if [ ! -d "$XCODEPROJ" ]; then
    if ! command -v xcodegen &>/dev/null; then
        echo "❌  LanMessenger.xcodeproj not found and xcodegen is not installed."
        echo "    Run:  brew install xcodegen"
        exit 1
    fi
    echo "▶  Generating Xcode project…"
    xcodegen generate --spec "$PROJECT_DIR/project.yml" --project "$PROJECT_DIR"
fi

mkdir -p "$BUILD_DIR/derived" "$RELEASES_DIR"

# ── 1. Build ──────────────────────────────────────────────────────────────────
echo "▶  Building Release…"
if ! xcodebuild \
    -project "$XCODEPROJ" \
    -scheme  LanMessenger \
    -configuration Release \
    -derivedDataPath "$BUILD_DIR/derived" \
    build 2>&1 | tee /tmp/xcodebuild.log | grep -E "^(error:|Build succeeded|BUILD FAILED)" ; then
    echo "❌  Build failed. Full log: /tmp/xcodebuild.log"
    exit 1
fi

# ── 2. Locate the .app ───────────────────────────────────────────────────────
APP_SRC=$(find "$BUILD_DIR/derived" \( -name "LAN Messenger.app" -o -name "LanMessenger.app" \) -maxdepth 8 | head -1)
if [ -z "$APP_SRC" ]; then
    echo "❌  .app bundle not found in derived data at $BUILD_DIR/derived"
    exit 1
fi

# ── 3. Copy to releases/macos/ ───────────────────────────────────────────────
echo "▶  Copying '$(basename "$APP_SRC")' to releases/macos/…"
rm -rf "$RELEASES_DIR/LAN Messenger.app" "$RELEASES_DIR/LanMessenger.app"
cp -R "$APP_SRC" "$RELEASES_DIR/"

# ── 4. Clean build artifacts ─────────────────────────────────────────────────
echo "▶  Removing build artifacts…"
rm -rf "$BUILD_DIR"

APP_DEST="$RELEASES_DIR/$(basename "$APP_SRC")"
echo ""
echo "✅  Done: $APP_DEST"
