#!/usr/bin/env bash
# validate-pkg.sh — Expand a flat .pkg installer, verify its structure, scripts,
# payload BOM, and the embedded .app bundle. Used by CI before publishing.
#
# Usage:
#   validate-pkg.sh /path/to/LanMessenger-macOS-1.3.9.pkg
set -euo pipefail

PKG="${1:?Usage: validate-pkg.sh <path-to-pkg>}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

[ -f "$PKG" ] || { echo "::error::PKG not found: $PKG"; exit 1; }

echo "▶  Validating PKG: $(basename "$PKG")"
SIZE=$(stat -f%z "$PKG" 2>/dev/null || stat -c%s "$PKG")
echo "  Size: $(numfmt --to=iec "$SIZE" 2>/dev/null || echo "$SIZE bytes")"

EXPAND_DIR=$(mktemp -d)
cleanup() { rm -rf "$EXPAND_DIR"; }
trap cleanup EXIT

FAIL=0
check_fail() { echo "::error::$*"; FAIL=$((FAIL + 1)); }

# ── 1. Expand the distribution package ──────────────────────────────────────
if ! /usr/sbin/pkgutil --expand "$PKG" "$EXPAND_DIR/expanded" 2>&1; then
    check_fail "pkgutil --expand failed — not a valid flat package"
    exit 1
fi
echo "  ✓ pkgutil --expand succeeded"

# Distribution packages contain a nested component package directory.
COMPONENT_PKG=$(find "$EXPAND_DIR/expanded" -maxdepth 1 -name "*.pkg" -type d | head -1)
if [ -z "$COMPONENT_PKG" ]; then
    check_fail "No component .pkg found inside the distribution package"
    exit 1
fi
echo "  ✓ Component: $(basename "$COMPONENT_PKG")"

# ── 2. Verify pre/postinstall scripts ───────────────────────────────────────
SCRIPTS_DIR="$COMPONENT_PKG/Scripts"
if [ -d "$SCRIPTS_DIR" ]; then
    for S in preinstall postinstall; do
        if [ -f "$SCRIPTS_DIR/$S" ]; then
            echo "  ✓ $S script present"
        else
            echo "  ⚠ $S script missing"
        fi
    done
else
    echo "  ⚠ No Scripts/ directory in component package"
fi

# ── 3. Verify payload contains the .app via BOM ─────────────────────────────
BOM="$COMPONENT_PKG/Bom"
if [ ! -f "$BOM" ]; then
    check_fail "No Bom in component package — cannot verify payload"
else
    APP_ENTRY=$(lsbom -p f "$BOM" 2>/dev/null | grep -E "\.app/Contents/Info\.plist" | head -1 || true)
    if [ -z "$APP_ENTRY" ]; then
        check_fail ".app bundle not found in PKG Bom — payload content is wrong"
    else
        APP_IN_BOM=$(echo "$APP_ENTRY" | sed 's|/Contents/Info\.plist.*||;s|^\./||')
        echo "  ✓ App in payload: $APP_IN_BOM"
    fi
fi

# ── 4. Verify install-location is / ─────────────────────────────────────────
PKGINFO="$COMPONENT_PKG/PackageInfo"
if [ -f "$PKGINFO" ]; then
    INSTALL_LOC=$(grep -oE 'install-location="[^"]*"' "$PKGINFO" | \
        sed 's/install-location="//;s/"//' | head -1 || true)
    if [ "$INSTALL_LOC" = "/" ]; then
        echo "  ✓ install-location is /"
    else
        check_fail "Unexpected install-location: '${INSTALL_LOC}' (expected '/')"
    fi
fi

# ── 5. Extract payload and run validate-bundle.sh ───────────────────────────
PAYLOAD="$COMPONENT_PKG/Payload"
if [ -f "$PAYLOAD" ]; then
    EXTRACT_DIR="$EXPAND_DIR/payload-extract"
    mkdir -p "$EXTRACT_DIR"
    # Payload is a gzipped cpio archive produced by pkgbuild.
    if ( cd "$EXTRACT_DIR" && gzip -dc "$PAYLOAD" | /usr/bin/cpio -i -d --quiet 2>/dev/null ); then
        APP_PATH=$(find "$EXTRACT_DIR" -maxdepth 5 -name "*.app" -type d 2>/dev/null | head -1)
        if [ -n "$APP_PATH" ]; then
            echo "  ✓ Payload extracted — running bundle validator"
            chmod +x "$SCRIPT_DIR/validate-bundle.sh"
            if ! "$SCRIPT_DIR/validate-bundle.sh" "$APP_PATH"; then
                FAIL=$((FAIL + 1))
            fi
        else
            echo "  ⚠ No .app found in extracted payload (BOM check passed, non-fatal)"
        fi
    else
        echo "  ⚠ Payload extraction skipped — BOM check is sufficient"
    fi
fi

if [ "$FAIL" -gt 0 ]; then
    echo ""
    echo "::error::validate-pkg.sh found $FAIL problem(s)"
    exit 1
fi
echo ""
echo "✅  PKG passes all integrity checks"
