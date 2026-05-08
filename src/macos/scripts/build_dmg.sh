#!/bin/bash
# build_dmg.sh — Build LAN Messenger.app and package it as a signed DMG.
#
# Usage (from lan-messenger-native/macos/):
#   ./scripts/build_dmg.sh
#
# Prerequisites:
#   - Xcode installed with valid code-signing identity
#   - xcodegen installed (brew install xcodegen)
#   - LanMessenger.xcodeproj generated (xcodegen generate)
#   - DEVELOPMENT_TEAM set in project.yml
#
# The script produces:
#   build/LAN Messenger-2.0.0.dmg
#
# To notarize after building:
#   xcrun notarytool submit "build/LAN Messenger-2.0.0.dmg" \
#       --apple-id "you@example.com" \
#       --team-id "XXXXXXXXXX" \
#       --password "@keychain:notarytool-password" \
#       --wait
#   xcrun stapler staple "build/LAN Messenger-2.0.0.dmg"

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_DIR="$PROJECT_DIR/build"
APP_NAME="LAN Messenger"
VERSION="2.0.0"
SCHEME="LanMessenger"
XCODEPROJ="$PROJECT_DIR/LanMessenger.xcodeproj"
ARCHIVE="$BUILD_DIR/LanMessenger.xcarchive"
EXPORT_DIR="$BUILD_DIR/export"
DMG_PATH="$BUILD_DIR/${APP_NAME}-${VERSION}.dmg"

# ── 0. Sanity checks ─────────────────────────────────────────────────────────
if [ ! -d "$XCODEPROJ" ]; then
    echo "❌  LanMessenger.xcodeproj not found."
    echo "    Run:  xcodegen generate"
    exit 1
fi

mkdir -p "$BUILD_DIR"

# ── 1. Archive ────────────────────────────────────────────────────────────────
echo "▶  Archiving…"
xcodebuild archive \
    -project "$XCODEPROJ" \
    -scheme  "$SCHEME" \
    -configuration Release \
    -archivePath "$ARCHIVE" \
    CODE_SIGN_STYLE=Automatic \
    | xcpretty 2>/dev/null || true   # xcpretty is optional; raw output on failure

# ── 2. Export .app ────────────────────────────────────────────────────────────
echo "▶  Exporting .app…"
cat > "$BUILD_DIR/export_options.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>method</key>
    <string>developer-id</string>
    <key>signingStyle</key>
    <string>automatic</string>
</dict>
</plist>
EOF

xcodebuild -exportArchive \
    -archivePath   "$ARCHIVE" \
    -exportPath    "$EXPORT_DIR" \
    -exportOptionsPlist "$BUILD_DIR/export_options.plist"

APP_PATH="$EXPORT_DIR/${APP_NAME}.app"
if [ ! -d "$APP_PATH" ]; then
    # Xcode sometimes uses the target name rather than the display name
    APP_PATH="$EXPORT_DIR/LanMessenger.app"
fi

# ── 3. Create DMG with hdiutil (no create-dmg dependency) ────────────────────
echo "▶  Creating DMG…"

# Temporary writable DMG
TMP_DMG="$BUILD_DIR/tmp_rw.dmg"
VOLUME_NAME="$APP_NAME $VERSION"

# Calculate needed size (app size + 20 MB padding)
APP_SIZE_MB=$(du -sm "$APP_PATH" | awk '{print $1}')
DMG_SIZE_MB=$(( APP_SIZE_MB + 20 ))

hdiutil create \
    -size        "${DMG_SIZE_MB}m" \
    -volname     "$VOLUME_NAME" \
    -srcfolder   "$APP_PATH" \
    -ov \
    -format      UDRW \
    "$TMP_DMG"

# Mount it, add /Applications symlink, set window position
MOUNT_DIR=$(hdiutil attach "$TMP_DMG" | grep "/Volumes/" | awk '{print $NF}')

# Create symlink to /Applications inside the DMG
ln -sf /Applications "$MOUNT_DIR/Applications"

# Optional: set a background image or window layout via AppleScript
osascript <<APPLESCRIPT
tell application "Finder"
    tell disk "$VOLUME_NAME"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set the bounds of container window to {400, 100, 920, 420}
        set theViewOptions to icon view options of container window
        set arrangement of theViewOptions to not arranged
        set icon size of theViewOptions to 128
        set position of item "${APP_NAME}.app" of container window to {140, 160}
        set position of item "Applications" of container window to {380, 160}
        close
        open
        update without registering applications
        delay 2
    end tell
end tell
APPLESCRIPT

hdiutil detach "$MOUNT_DIR"

# Convert to compressed read-only DMG
hdiutil convert "$TMP_DMG" \
    -format UDZO \
    -o     "$DMG_PATH"

rm -f "$TMP_DMG"

echo ""
echo "✅  Done: $DMG_PATH"
echo ""
echo "   Next steps for notarization:"
echo "   xcrun notarytool submit \"$DMG_PATH\" \\"
echo "       --apple-id  \"you@example.com\" \\"
echo "       --team-id   \"XXXXXXXXXX\" \\"
echo "       --password  \"@keychain:notarytool-password\" \\"
echo "       --wait"
echo "   xcrun stapler staple \"$DMG_PATH\""
