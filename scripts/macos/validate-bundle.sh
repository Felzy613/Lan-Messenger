#!/usr/bin/env bash
# validate-bundle.sh — Structural + branding integrity check for a .app bundle.
#
# Usage:
#   validate-bundle.sh /path/to/LAN\ Messenger.app
#
# Exits 0 only if every check passes. Any failure prints a "::error::" line that
# GitHub Actions will surface as a job failure annotation.
set -euo pipefail

APP="${1:?Usage: validate-bundle.sh <path-to-app-bundle>}"
if [ ! -d "$APP" ]; then
    echo "::error::Bundle not found: $APP"
    exit 1
fi

FAIL=0
check_fail() { echo "::error::$*"; FAIL=$((FAIL + 1)); }
check_ok()   { echo "  ✓ $*"; }

echo "▶  Validating bundle: $APP"

# ── 1. Bundle layout ─────────────────────────────────────────────────────────
INFO_PLIST="$APP/Contents/Info.plist"
if [ ! -f "$INFO_PLIST" ]; then
    check_fail "Missing Contents/Info.plist"
else
    check_ok "Contents/Info.plist present"
fi

EXECUTABLE_NAME=$(/usr/libexec/PlistBuddy -c "Print :CFBundleExecutable" "$INFO_PLIST" 2>/dev/null || echo "")
EXECUTABLE="$APP/Contents/MacOS/$EXECUTABLE_NAME"
if [ -z "$EXECUTABLE_NAME" ]; then
    check_fail "Info.plist missing CFBundleExecutable"
elif [ ! -x "$EXECUTABLE" ]; then
    check_fail "Executable missing or not executable: $EXECUTABLE"
else
    check_ok "Executable: $EXECUTABLE_NAME ($(file -b "$EXECUTABLE" | head -c 80))"
fi

# ── 2. Info.plist keys ───────────────────────────────────────────────────────
for KEY in \
    CFBundleIdentifier \
    CFBundleName \
    CFBundleDisplayName \
    CFBundleShortVersionString \
    CFBundleVersion \
    LSMinimumSystemVersion \
    NSLocalNetworkUsageDescription \
    NSBonjourServices
do
    VAL=$(/usr/libexec/PlistBuddy -c "Print :$KEY" "$INFO_PLIST" 2>/dev/null || echo "")
    if [ -z "$VAL" ]; then
        check_fail "Info.plist missing $KEY"
    else
        # PlistBuddy prints arrays as multi-line; truncate display.
        check_ok "$KEY = $(printf '%s' "$VAL" | tr '\n' ' ' | head -c 80)"
    fi
done

# ── 3. AppIcon resources ─────────────────────────────────────────────────────
# When Asset Catalog compiles correctly, AppIcon ends up as
#   Contents/Resources/AppIcon.icns
# AND/OR baked into Assets.car (newer Xcode toolchains do both). We accept
# either, but at least one must be present and large enough to be real artwork.
ICNS="$APP/Contents/Resources/AppIcon.icns"
CAR="$APP/Contents/Resources/Assets.car"
ICON_OK=0

if [ -f "$ICNS" ]; then
    SIZE=$(stat -f%z "$ICNS" 2>/dev/null || stat -c%s "$ICNS")
    if [ "$SIZE" -lt 50000 ]; then
        check_fail "AppIcon.icns is suspiciously small ($SIZE bytes) — likely a missing-icon fallback"
    else
        check_ok "AppIcon.icns present ($SIZE bytes)"
        ICON_OK=1
        # Verify the icns is actually a valid icon file.
        if /usr/bin/file "$ICNS" | grep -qi "Mac OS X icon"; then
            check_ok "AppIcon.icns is a valid icns container"
        else
            check_fail "AppIcon.icns failed file-type detection"
        fi
    fi
fi

if [ -f "$CAR" ]; then
    SIZE=$(stat -f%z "$CAR" 2>/dev/null || stat -c%s "$CAR")
    if [ "$SIZE" -lt 50000 ]; then
        check_fail "Assets.car is suspiciously small ($SIZE bytes)"
    else
        check_ok "Assets.car present ($SIZE bytes)"
        ICON_OK=1
        # `assetutil --info` enumerates the rendition table; we just need a
        # non-empty list that contains AppIcon entries.
        if command -v assetutil >/dev/null 2>&1; then
            APPICON_RENDITIONS=$(/usr/bin/assetutil --info "$CAR" 2>/dev/null | \
                grep -ic '"Name" : "AppIcon"' || true)
            if [ "$APPICON_RENDITIONS" -lt 1 ]; then
                check_fail "Assets.car has no AppIcon renditions"
            else
                check_ok "Assets.car has $APPICON_RENDITIONS AppIcon rendition(s)"
            fi
        fi
    fi
fi

if [ "$ICON_OK" -eq 0 ]; then
    check_fail "Bundle has no AppIcon — neither AppIcon.icns nor Assets.car was found"
fi

# CFBundleIconFile / CFBundleIconName in Info.plist tells Finder which icon to
# show. Xcode usually fills CFBundleIconName from the asset catalog name.
ICON_FILE=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconFile' "$INFO_PLIST" 2>/dev/null || echo "")
ICON_NAME=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconName' "$INFO_PLIST" 2>/dev/null || echo "")
if [ -z "$ICON_FILE" ] && [ -z "$ICON_NAME" ]; then
    check_fail "Info.plist references no icon (need CFBundleIconFile or CFBundleIconName)"
else
    check_ok "Icon reference: file='$ICON_FILE' name='$ICON_NAME'"
fi

# ── 4. Code signing ─────────────────────────────────────────────────────────
SIG_OUT=$(/usr/bin/codesign --display --verbose=2 "$APP" 2>&1 || true)
if echo "$SIG_OUT" | grep -q "code object is not signed"; then
    check_fail "App is not code-signed"
else
    SIGNED_BY=$(echo "$SIG_OUT" | awk -F'=' '/^Authority/{print $2; exit}')
    if [ -z "$SIGNED_BY" ]; then
        SIGNED_BY=$(echo "$SIG_OUT" | grep -i '^Signature=' | head -1)
    fi
    check_ok "Signed: ${SIGNED_BY:-(ad-hoc)}"
fi

if ! /usr/bin/codesign --verify --deep --strict "$APP" 2>&1; then
    check_fail "codesign --verify --deep --strict failed"
else
    check_ok "Deep signature verifies"
fi

echo ""
if [ "$FAIL" -gt 0 ]; then
    echo "::error::validate-bundle.sh found $FAIL problem(s)"
    exit 1
fi
echo "✅  Bundle passes all integrity checks"
