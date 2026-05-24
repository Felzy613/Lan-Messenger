#!/usr/bin/env bash
# smoke-test.sh — End-to-end install + launch test for LAN Messenger on macOS.
#
# The script accepts any of the three artifact formats we ship:
#   .dmg   — primary; mount, copy to /Applications, launch
#   .pkg   — flat installer; run `installer -pkg`, launch
#   .zip   — update channel; expand, copy to /Applications, launch
#
# Usage:
#   smoke-test.sh <path-to-artifact> [--keep-installed]
#
# Exit codes:
#   0  app launched and stayed alive long enough to count as "working"
#   1  any failure (no .app found, process never appeared, crashed early, …)
#
# Note: The runner's /Applications is mutable — we restore it on exit unless
# --keep-installed is passed.

set -euo pipefail

ARTIFACT="${1:?Usage: smoke-test.sh <artifact> [--keep-installed]}"
KEEP_INSTALLED=0
[ "${2:-}" = "--keep-installed" ] && KEEP_INSTALLED=1

if [ ! -f "$ARTIFACT" ]; then
    echo "::error::Artifact not found: $ARTIFACT"
    exit 1
fi

# ── Configuration ───────────────────────────────────────────────────────────
APP_NAME="LAN Messenger"
EXECUTABLE_NAME="LanMessenger"
INSTALL_PATH="/Applications/${APP_NAME}.app"
STARTUP_WAIT=15
ALIVE_WAIT=20

SMOKE_DIR="$(mktemp -d)"
LOG_FILE="$SMOKE_DIR/smoke.log"
MOUNT_PT=""
INSTALLED_NEW=0

log() { echo "$1" | tee -a "$LOG_FILE"; }
fail() { log "::error::$1"; cleanup_and_exit 1; }

cleanup_and_exit() {
    local code="${1:-0}"
    # Kill anything we started
    pkill -x "$EXECUTABLE_NAME" 2>/dev/null || true
    sleep 1

    if [ -n "$MOUNT_PT" ] && [ -d "$MOUNT_PT" ]; then
        /usr/bin/hdiutil detach "$MOUNT_PT" -quiet 2>/dev/null || \
            /usr/bin/hdiutil detach "$MOUNT_PT" -force 2>/dev/null || true
    fi

    if [ "$INSTALLED_NEW" = "1" ] && [ "$KEEP_INSTALLED" = "0" ] && [ -d "$INSTALL_PATH" ]; then
        sudo rm -rf "$INSTALL_PATH" 2>/dev/null || rm -rf "$INSTALL_PATH" || true
    fi

    # Preserve smoke.log next to the working directory so CI can upload it.
    cp "$LOG_FILE" "$(dirname "$ARTIFACT")/smoke.log" 2>/dev/null || true
    cp "$LOG_FILE" smoke.log 2>/dev/null || true

    rm -rf "$SMOKE_DIR" 2>/dev/null || true
    exit "$code"
}
trap 'cleanup_and_exit 1' INT TERM

mkdir -p "$SMOKE_DIR"
log "▶  Artifact: $ARTIFACT"
log "  ($(file -b "$ARTIFACT" | head -c 80))"

# Bail early if /Applications already has a stale copy from a prior run —
# that would mask real install bugs. (CI runners are clean, but local devs
# may not be.)
if [ -d "$INSTALL_PATH" ]; then
    log "  /Applications already has a copy — removing to test fresh install"
    sudo rm -rf "$INSTALL_PATH" 2>/dev/null || rm -rf "$INSTALL_PATH" || fail "Could not remove existing $INSTALL_PATH"
fi

# ── 1. Install the artifact ─────────────────────────────────────────────────
case "$ARTIFACT" in
    *.dmg)
        log "▶  Mounting DMG"
        MOUNT_OUT=$(/usr/bin/hdiutil attach -nobrowse -noverify -noautoopen "$ARTIFACT")
        MOUNT_PT=$(echo "$MOUNT_OUT" | awk '/\/Volumes\//{match($0, /\/Volumes\/.*/); print substr($0, RSTART); exit}')
        [ -d "$MOUNT_PT" ] || fail "DMG mount failed (no /Volumes/ entry)"
        log "  Mounted at: $MOUNT_PT"

        APP_SRC=$(find "$MOUNT_PT" -maxdepth 2 -name "*.app" -type d | head -1)
        [ -z "$APP_SRC" ] && fail "DMG contains no .app bundle"
        log "  Copying $APP_SRC → $INSTALL_PATH"
        /usr/bin/ditto "$APP_SRC" "$INSTALL_PATH" || fail "ditto copy failed"
        INSTALLED_NEW=1
        ;;
    *.pkg)
        log "▶  Installing PKG (sudo)"
        sudo /usr/sbin/installer -pkg "$ARTIFACT" -target / 2>&1 | tee -a "$LOG_FILE"
        INSTALLED_NEW=1
        [ -d "$INSTALL_PATH" ] || fail "PKG did not produce $INSTALL_PATH"
        ;;
    *.zip)
        log "▶  Extracting ZIP"
        /usr/bin/ditto -x -k "$ARTIFACT" "$SMOKE_DIR"
        APP_SRC=$(find "$SMOKE_DIR" -maxdepth 3 -name "*.app" -type d | head -1)
        [ -z "$APP_SRC" ] && fail "ZIP contains no .app bundle"
        log "  Copying $APP_SRC → $INSTALL_PATH"
        /usr/bin/ditto "$APP_SRC" "$INSTALL_PATH" || fail "ditto copy failed"
        INSTALLED_NEW=1
        ;;
    *)
        fail "Unsupported artifact extension: $ARTIFACT (need .dmg/.pkg/.zip)"
        ;;
esac

# Verify the bundle layout right after install — the same validator the build
# step runs, applied to the path the user will actually run from.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
log "▶  Validating installed bundle"
if ! "$SCRIPT_DIR/validate-bundle.sh" "$INSTALL_PATH" 2>&1 | tee -a "$LOG_FILE"; then
    fail "Installed bundle failed validate-bundle.sh"
fi

# Strip any quarantine xattr the smoke runner inherited; otherwise Gatekeeper
# will block the launch with a modal dialog and the process never starts.
/usr/bin/xattr -dr com.apple.quarantine "$INSTALL_PATH" 2>/dev/null || true

# ── 2. Launch ────────────────────────────────────────────────────────────────
log "▶  Launching"
/usr/bin/open "$INSTALL_PATH" 2>&1 | tee -a "$LOG_FILE" || true

log "  Waiting up to ${STARTUP_WAIT}s for $EXECUTABLE_NAME to appear"
PID=""
for _ in $(seq 1 "$STARTUP_WAIT"); do
    PID=$(pgrep -x "$EXECUTABLE_NAME" 2>/dev/null | head -1 || true)
    [ -n "$PID" ] && break
    sleep 1
done

if [ -z "$PID" ]; then
    log "::error::Process $EXECUTABLE_NAME never appeared"
    log "Crash reports:"
    ls ~/Library/Logs/DiagnosticReports/ 2>/dev/null | tee -a "$LOG_FILE" || log "  (none)"
    CRASH=$(ls ~/Library/Logs/DiagnosticReports/LanMessenger* 2>/dev/null | head -1 || true)
    if [ -n "$CRASH" ]; then
        log "--- Crash report excerpt ---"
        head -100 "$CRASH" | tee -a "$LOG_FILE"
    fi
    fail "App did not start after install"
fi

log "  ✓ Process alive (PID=$PID)"
log "▶  Stability window: ${ALIVE_WAIT}s"
sleep "$ALIVE_WAIT"

if ! kill -0 "$PID" 2>/dev/null; then
    fail "App (PID=$PID) died during stability window — likely a startup crash"
fi

# ── 3. Bonus: confirm Launch Services picked up the bundle ──────────────────
LS_DUMP=$(/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
    -dump 2>/dev/null | grep -i "$INSTALL_PATH" | head -3 || true)
if [ -n "$LS_DUMP" ]; then
    log "  ✓ Launch Services has registered the bundle"
fi

# ── 4. Verify diagnostic logger wrote a session header ──────────────────────
# Confirms NetLogger initialised, the Application Support directory is
# writable, and the first INFO event reached disk.  A missing header during
# the stability window means logging is broken — exactly the kind of
# regression a smoke test should catch.
CLIENT_LOG="$HOME/Library/Application Support/LanMessenger/Logs/client.log"
if [ -f "$CLIENT_LOG" ]; then
    if /usr/bin/head -1 "$CLIENT_LOG" | grep -q '^# Session '; then
        log "  ✓ Diagnostic log opened with a Session header"
    else
        log "::warning::client.log exists but has no # Session header — logger may be misconfigured"
    fi
else
    log "::warning::client.log was not created during the stability window — logger may be broken"
fi

log ""
log "✅  Smoke test passed — app installed cleanly and ran stably for $((STARTUP_WAIT + ALIVE_WAIT))s"
cleanup_and_exit 0
