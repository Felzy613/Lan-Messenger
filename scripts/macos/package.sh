#!/usr/bin/env bash
# package.sh — End-to-end macOS packaging pipeline.
#
# Build → sign → notarize (optional) → PKG + ZIP + SHA256 sidecars.
#
# Designed to run in two contexts:
#   - GitHub Actions  (no env var work needed; secrets get exported by the workflow)
#   - Developer laptop  (set DEVELOPER_ID_APPLICATION and friends in your shell)
#
# Required environment variables (must be set by caller):
#   VERSION           Marketing version, e.g. "1.3.9"
#
# Optional environment variables:
#   SIGNING_IDENTITY  Code-signing identity. Pass an empty string or omit for ad-hoc
#                     signing (produces a runnable but Gatekeeper-warned bundle).
#                     Example: "Developer ID Application: Dave Felzy (7FAZT3258V)"
#   NOTARIZE          "1" to notarize and staple the PKG. Requires NOTARY_*.
#   NOTARY_APPLE_ID   Apple ID e-mail for notarytool.
#   NOTARY_TEAM_ID    Team ID for notarytool.
#   NOTARY_PASSWORD   App-specific password for notarytool.
#   ENTITLEMENTS      Path to the .entitlements plist (defaults to the bundled one).
#   OUTPUT_DIR        Where finished artifacts land (defaults to <repo>/dist/macos).
#   KEEP_BUILD        "1" to keep the build/ directory for inspection.
#
# Artifacts produced in $OUTPUT_DIR:
#   LanMessenger-macOS-${VERSION}.pkg          Primary installer (double-click to install)
#   LanMessenger-macOS-${VERSION}.pkg.sha256
#   LanMessenger-macOS-${VERSION}.zip          Update channel artifact (top-level: LanMessenger.app)
#   LanMessenger-macOS-${VERSION}.zip.sha256
#
# Exit codes:
#   0   all artifacts produced
#   1+  fatal error; partial artifacts are left in $OUTPUT_DIR for inspection

set -euo pipefail

# ── 0. Resolve paths ──────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MAC_DIR="$REPO_ROOT/src/macos"
BUILD_DIR="$MAC_DIR/build"
ARCHIVE_PATH="$BUILD_DIR/LanMessenger.xcarchive"
EXPORT_DIR="$BUILD_DIR/export"
STAGING_DIR="$BUILD_DIR/staging"

OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT/dist/macos}"
ENTITLEMENTS="${ENTITLEMENTS:-$MAC_DIR/LanMessenger/LanMessenger.entitlements}"

: "${VERSION:?VERSION must be set (e.g. VERSION=1.3.9)}"

APP_NAME_DISPLAY="LAN Messenger"   # what the user sees in Finder
APP_NAME_BUNDLE="LanMessenger"      # what xcodebuild emits as <PRODUCT_NAME>.app
ARTIFACT_BASE="LanMessenger-macOS-${VERSION}"

SIGNING_IDENTITY="${SIGNING_IDENTITY-}"
NOTARIZE="${NOTARIZE:-0}"
KEEP_BUILD="${KEEP_BUILD:-0}"

# ── 1. Ensure host is macOS ───────────────────────────────────────────────────
if [ "$(uname)" != "Darwin" ]; then
    echo "::error::package.sh must run on macOS (current uname: $(uname))"
    exit 1
fi

cleanup() {
    [ "$KEEP_BUILD" = "1" ] || rm -rf "$BUILD_DIR"
}
trap cleanup EXIT

# Tee everything we say to BOTH the console and a packaging log that the CI
# uploads on failure — much easier than scraping the GitHub Actions log.
PKG_LOG="$BUILD_DIR/package.log"
mkdir -p "$BUILD_DIR" "$OUTPUT_DIR" "$STAGING_DIR"
exec > >(tee -a "$PKG_LOG") 2>&1

step() { echo ""; echo "▶  $*"; }

step "Pipeline starting — version $VERSION, signing=${SIGNING_IDENTITY:+yes (real)}${SIGNING_IDENTITY:-yes (ad-hoc)}, notarize=$NOTARIZE, artifacts=PKG+ZIP"

# ── 2. Generate Xcode project from project.yml ────────────────────────────────
step "Generating Xcode project (xcodegen)"
cd "$MAC_DIR"
# Stamp the full MAJOR.MINOR.PATCH version into project.yml so xcodegen and
# xcodebuild both pick it up. CFBundleShortVersionString supports full X.Y.Z.
MARKETING_VERSION="$VERSION"
sed -i '' "s/MARKETING_VERSION:.*/MARKETING_VERSION: \"$MARKETING_VERSION\"/" project.yml
xcodegen generate >/dev/null

# ── 3. Build Release ─────────────────────────────────────────────────────────
# xcodebuild archive silently drops Contents/Resources (compiled asset catalog)
# when code signing is ad-hoc. A plain Release build to derivedData produces a
# complete bundle and is what the old workflow used successfully.
step "Building Release (xcodebuild)"
DERIVED_DIR="$BUILD_DIR/derived"
xcodebuild build \
    -project "$MAC_DIR/LanMessenger.xcodeproj" \
    -scheme  LanMessenger \
    -configuration Release \
    -derivedDataPath "$DERIVED_DIR" \
    -destination 'generic/platform=macOS' \
    CODE_SIGN_STYLE=Manual \
    CODE_SIGN_IDENTITY="-" \
    ONLY_ACTIVE_ARCH=NO \
    MARKETING_VERSION="$MARKETING_VERSION" \
    CURRENT_PROJECT_VERSION="${VERSION##*.}" \
    SWIFT_OPTIMIZATION_LEVEL='-O' \
    GCC_OPTIMIZATION_LEVEL='s' \
    2>&1 | grep -E "error:|Build complete|FAILED" || true

APP_IN_BUILD=$(find "$DERIVED_DIR/Build/Products/Release" -name "${APP_NAME_BUNDLE}.app" -type d 2>/dev/null | head -1)
if [ -z "$APP_IN_BUILD" ]; then
    echo "::error::${APP_NAME_BUNDLE}.app not found in derived data"
    find "$DERIVED_DIR/Build" -name "*.app" -maxdepth 5 2>/dev/null || true
    exit 1
fi

# Copy to EXPORT_DIR — we sign + ship from there.
rm -rf "$EXPORT_DIR"
mkdir -p "$EXPORT_DIR"
cp -R "$APP_IN_BUILD" "$EXPORT_DIR/"
APP_PATH="$EXPORT_DIR/${APP_NAME_BUNDLE}.app"

step "Inspecting bundle"
echo "  Executable:  $(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP_PATH/Contents/Info.plist")"
echo "  Identifier:  $(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP_PATH/Contents/Info.plist")"
echo "  Version:     $(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP_PATH/Contents/Info.plist")"
echo "  Bundle size: $(du -sh "$APP_PATH" | awk '{print $1}')"
echo "  Contents:"
find "$APP_PATH/Contents" -maxdepth 2 | sort || true

# ── 4. Code-sign ──────────────────────────────────────────────────────────────
# Deep-sign with the hardened runtime. We sign every embedded framework/binary
# bottom-up so the outer envelope is signed last (Apple's recommended order).
step "Code-signing"
if [ -n "$SIGNING_IDENTITY" ]; then
    echo "  Identity: $SIGNING_IDENTITY"
    /usr/bin/codesign --force --options runtime --timestamp \
        --entitlements "$ENTITLEMENTS" \
        --sign "$SIGNING_IDENTITY" \
        --deep \
        "$APP_PATH"
else
    # Ad-hoc signing (identity "-"). Produces a working bundle that Gatekeeper
    # will refuse on first launch unless the user explicitly opens it. This
    # mode exists for local development and unsigned CI runs.
    echo "  Identity: ad-hoc (-)"
    /usr/bin/codesign --force --sign "-" --deep "$APP_PATH"
fi

step "Verifying signature"
if ! /usr/bin/codesign --verify --deep --strict --verbose=2 "$APP_PATH" 2>&1 | tail -6; then
    echo "::error::codesign verification failed"
    exit 1
fi

# ── 5. Notarize (real signing only) ──────────────────────────────────────────
if [ "$NOTARIZE" = "1" ] && [ -n "$SIGNING_IDENTITY" ]; then
    step "Submitting for notarization"
    : "${NOTARY_APPLE_ID:?NOTARY_APPLE_ID must be set when NOTARIZE=1}"
    : "${NOTARY_TEAM_ID:?NOTARY_TEAM_ID must be set when NOTARIZE=1}"
    : "${NOTARY_PASSWORD:?NOTARY_PASSWORD must be set when NOTARIZE=1}"

    NOTARIZE_ZIP="$BUILD_DIR/notarize.zip"
    /usr/bin/ditto -c -k --keepParent "$APP_PATH" "$NOTARIZE_ZIP"

    xcrun notarytool submit "$NOTARIZE_ZIP" \
        --apple-id   "$NOTARY_APPLE_ID" \
        --team-id    "$NOTARY_TEAM_ID" \
        --password   "$NOTARY_PASSWORD" \
        --wait

    xcrun stapler staple "$APP_PATH"
    rm -f "$NOTARIZE_ZIP"
elif [ "$NOTARIZE" = "1" ]; then
    echo "::warning::NOTARIZE=1 but no SIGNING_IDENTITY — skipping notarization"
fi

# ── 6. Stage the user-facing .app ────────────────────────────────────────────
# We rename the on-disk bundle to "$APP_NAME_DISPLAY.app" (with a space) so the
# DMG and Finder show the marketing name. The CFBundleDisplayName key inside
# Info.plist already says "LAN Messenger", so the rename is purely cosmetic
# for the on-disk filename and doesn't change CFBundleExecutable.
step "Staging final .app as '${APP_NAME_DISPLAY}.app'"
APP_DISPLAY="$STAGING_DIR/${APP_NAME_DISPLAY}.app"
rm -rf "$APP_DISPLAY"
/usr/bin/ditto "$APP_PATH" "$APP_DISPLAY"

# Strip extended attributes (quarantine, etc.) that survived the build
/usr/bin/xattr -cr "$APP_DISPLAY" || true

# ── 7. Build the ZIP (used by the in-app updater) ────────────────────────────
step "Building update-channel ZIP"
ZIP_PATH="$OUTPUT_DIR/${ARTIFACT_BASE}.zip"
rm -f "$ZIP_PATH"
( cd "$STAGING_DIR" && /usr/bin/ditto -c -k --keepParent --sequesterRsrc \
    "${APP_NAME_DISPLAY}.app" "$ZIP_PATH" )
echo "  $(basename "$ZIP_PATH") $(du -h "$ZIP_PATH" | awk '{print $1}')"

# ── 8. Build the PKG (primary user-facing installer) ─────────────────────────
step "Building PKG"
PKG_PATH="$OUTPUT_DIR/${ARTIFACT_BASE}.pkg"
PKG_BUILD_ROOT="$BUILD_DIR/pkg-root"
PKG_SCRIPTS_DIR="$BUILD_DIR/pkg-scripts"
rm -rf "$PKG_BUILD_ROOT" "$PKG_SCRIPTS_DIR"
mkdir -p "$PKG_BUILD_ROOT/Applications" "$PKG_SCRIPTS_DIR"

/usr/bin/ditto "$APP_DISPLAY" "$PKG_BUILD_ROOT/Applications/${APP_NAME_DISPLAY}.app"

# preinstall: gracefully terminate any running copy so it doesn't hold file
# descriptors open while the installer lays new files into /Applications.
cat > "$PKG_SCRIPTS_DIR/preinstall" <<'PREINSTALL'
#!/bin/bash
# preinstall — run before macOS lays new files into /Applications.
# Quietly kill running copies so they don't hold file descriptors open.
APP="/Applications/LAN Messenger.app"
if pgrep -x LanMessenger >/dev/null 2>&1; then
    pkill -TERM -x LanMessenger 2>/dev/null || true
    # Give the process a moment to flush state to disk before SIGKILL.
    for _ in 1 2 3 4 5; do
        sleep 1
        pgrep -x LanMessenger >/dev/null 2>&1 || break
    done
    pkill -KILL -x LanMessenger 2>/dev/null || true
fi
exit 0
PREINSTALL

# postinstall: clear quarantine xattr (otherwise the user gets a Gatekeeper
# dialog despite having explicitly run the installer) and re-register the
# bundle with Launch Services so Spotlight/Finder pick it up immediately.
cat > "$PKG_SCRIPTS_DIR/postinstall" <<'POSTINSTALL'
#!/bin/bash
APP="/Applications/LAN Messenger.app"
# Strip quarantine that the installer might have inherited from the
# downloaded .pkg envelope.
/usr/bin/xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
# Register the bundle so Spotlight / Launchpad / Open With surface it
# without waiting for the periodic Launch Services rebuild.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
    -f -R "$APP" >/dev/null 2>&1 || true
exit 0
POSTINSTALL

chmod 755 "$PKG_SCRIPTS_DIR/preinstall" "$PKG_SCRIPTS_DIR/postinstall"

COMPONENT_PKG="$BUILD_DIR/component.pkg"
/usr/bin/pkgbuild \
    --root      "$PKG_BUILD_ROOT" \
    --install-location "/" \
    --identifier "com.dave.lanmessenger.installer" \
    --version   "$VERSION" \
    --scripts   "$PKG_SCRIPTS_DIR" \
    "$COMPONENT_PKG" >/dev/null

# Wrap in a distribution package so the GUI installer shows the product name
# and enforces the minimum OS version.
DISTRIBUTION_XML="$BUILD_DIR/distribution.xml"
cat > "$DISTRIBUTION_XML" <<DISTRIBUTION
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>LAN Messenger ${VERSION}</title>
    <organization>com.dave.lanmessenger</organization>
    <domains enable_localSystem="true"/>
    <options customize="never" require-scripts="false" rootVolumeOnly="true"/>
    <volume-check>
        <allowed-os-versions>
            <os-version min="13.0"/>
        </allowed-os-versions>
    </volume-check>
    <choices-outline>
        <line choice="default">
            <line choice="com.dave.lanmessenger.installer"/>
        </line>
    </choices-outline>
    <choice id="default"/>
    <choice id="com.dave.lanmessenger.installer" visible="false" title="LAN Messenger">
        <pkg-ref id="com.dave.lanmessenger.installer"/>
    </choice>
    <pkg-ref id="com.dave.lanmessenger.installer" version="${VERSION}" onConclusion="none">component.pkg</pkg-ref>
</installer-gui-script>
DISTRIBUTION

# productbuild --sign requires a "Developer ID Installer" certificate, which is
# distinct from the "Developer ID Application" cert used for the .app. Only sign
# when INSTALLER_SIGNING_IDENTITY is explicitly provided; otherwise ship unsigned.
INSTALLER_SIGNING_IDENTITY="${INSTALLER_SIGNING_IDENTITY-}"
if [ -n "$INSTALLER_SIGNING_IDENTITY" ]; then
    echo "  Signing PKG with installer identity: $INSTALLER_SIGNING_IDENTITY"
    if /usr/bin/productbuild \
            --distribution "$DISTRIBUTION_XML" \
            --package-path "$BUILD_DIR" \
            --sign "$INSTALLER_SIGNING_IDENTITY" \
            "$PKG_PATH" >/dev/null; then
        if [ "$NOTARIZE" = "1" ]; then
            echo "  Notarizing PKG"
            xcrun notarytool submit "$PKG_PATH" \
                --apple-id "$NOTARY_APPLE_ID" \
                --team-id  "$NOTARY_TEAM_ID" \
                --password "$NOTARY_PASSWORD" \
                --wait
            xcrun stapler staple "$PKG_PATH"
        fi
    else
        echo "::warning::productbuild --sign failed — emitting unsigned PKG"
        /usr/bin/productbuild \
            --distribution "$DISTRIBUTION_XML" \
            --package-path "$BUILD_DIR" \
            "$PKG_PATH" >/dev/null
    fi
else
    /usr/bin/productbuild \
        --distribution "$DISTRIBUTION_XML" \
        --package-path "$BUILD_DIR" \
        "$PKG_PATH" >/dev/null
fi
echo "  $(basename "$PKG_PATH") $(du -h "$PKG_PATH" | awk '{print $1}')"

# ── 9. SHA256 sidecars ───────────────────────────────────────────────────────
step "Writing SHA256 sidecars"
write_sidecar() {
    local path="$1"
    [ -f "$path" ] || return 0
    local hash
    hash=$(/usr/bin/shasum -a 256 "$path" | awk '{print $1}')
    printf '%s  %s\n' "$hash" "$(basename "$path")" > "${path}.sha256"
    echo "  $(basename "$path").sha256  $hash"
}
write_sidecar "$PKG_PATH"
write_sidecar "$ZIP_PATH"

# ── 10. Final inventory ──────────────────────────────────────────────────────
step "Done"
( cd "$OUTPUT_DIR" && ls -lh "${ARTIFACT_BASE}".* )
echo ""
echo "✅  Artifacts in $OUTPUT_DIR"
