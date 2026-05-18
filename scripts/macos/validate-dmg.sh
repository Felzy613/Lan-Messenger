#!/usr/bin/env bash
# validate-dmg.sh — Mount a DMG, verify its branding/layout, run validate-bundle.sh
# on the .app inside, and unmount cleanly. Used by CI before publishing.
#
# Usage:
#   validate-dmg.sh /path/to/LanMessenger-macOS-1.3.9.dmg
set -euo pipefail

DMG="${1:?Usage: validate-dmg.sh <path-to-dmg>}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

if [ ! -f "$DMG" ]; then
    echo "::error::DMG not found: $DMG"
    exit 1
fi

echo "▶  Validating DMG: $(basename "$DMG")"
SIZE=$(stat -f%z "$DMG" 2>/dev/null || stat -c%s "$DMG")
echo "  Size: $(numfmt --to=iec "$SIZE" 2>/dev/null || echo "$SIZE bytes")"

# hdiutil verify catches DMG-level corruption (bad checksums, truncated images).
if ! /usr/bin/hdiutil verify "$DMG" >/dev/null 2>&1; then
    echo "::error::hdiutil verify failed — DMG is corrupt"
    exit 1
fi
echo "  ✓ hdiutil verify passed"

# Mount in a private mount point — `-nobrowse` keeps Finder from popping the
# disk on the runner's desktop.
MOUNT_OUT=$(/usr/bin/hdiutil attach -nobrowse -noverify -noautoopen "$DMG")
MOUNT_PT=$(echo "$MOUNT_OUT" | awk '/\/Volumes\//{print $NF; exit}')
if [ -z "$MOUNT_PT" ] || [ ! -d "$MOUNT_PT" ]; then
    echo "::error::Could not determine mount point"
    echo "$MOUNT_OUT"
    exit 1
fi
echo "  Mounted at: $MOUNT_PT"

cleanup() {
    /usr/bin/hdiutil detach "$MOUNT_PT" -quiet 2>/dev/null || \
        /usr/bin/hdiutil detach "$MOUNT_PT" -force 2>/dev/null || true
}
trap cleanup EXIT

# ── Layout checks ────────────────────────────────────────────────────────────
FAIL=0
check_fail() { echo "::error::$*"; FAIL=$((FAIL + 1)); }

APP_IN_DMG=$(find "$MOUNT_PT" -maxdepth 2 -name "*.app" -type d | head -1)
if [ -z "$APP_IN_DMG" ]; then
    check_fail "No .app bundle at the root of the DMG"
else
    echo "  ✓ App: $(basename "$APP_IN_DMG")"
fi

# /Applications symlink is the cue Finder users follow to install. Without it,
# the DMG is just a folder full of files.
if [ ! -L "$MOUNT_PT/Applications" ]; then
    check_fail "Missing /Applications symlink — DMG is not a drag-to-install installer"
else
    APPLICATIONS_TARGET=$(readlink "$MOUNT_PT/Applications")
    if [ "$APPLICATIONS_TARGET" != "/Applications" ]; then
        check_fail "/Applications symlink points to '$APPLICATIONS_TARGET' (expected '/Applications')"
    else
        echo "  ✓ /Applications symlink correct"
    fi
fi

# Volume icon — Finder shows .VolumeIcon.icns when the volume has the "C"
# attribute. Not strictly required, but its absence is a regression for us.
if [ -f "$MOUNT_PT/.VolumeIcon.icns" ]; then
    echo "  ✓ Volume has custom .VolumeIcon.icns"
else
    echo "  ⚠ DMG has no custom volume icon — Finder will use the generic disk icon"
fi

# ── Run the bundle validator on the embedded .app ────────────────────────────
if [ -n "${APP_IN_DMG:-}" ]; then
    chmod +x "$SCRIPT_DIR/validate-bundle.sh"
    if ! "$SCRIPT_DIR/validate-bundle.sh" "$APP_IN_DMG"; then
        FAIL=$((FAIL + 1))
    fi
fi

if [ "$FAIL" -gt 0 ]; then
    echo ""
    echo "::error::validate-dmg.sh found $FAIL problem(s)"
    exit 1
fi
echo ""
echo "✅  DMG passes all integrity checks"
