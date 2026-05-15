#!/usr/bin/env bash
# Startup smoke test for LAN Messenger macOS
# Usage: smoke-test.sh <path-to-zip>
# Exit 0: app launched and remained alive; Exit 1: crash or no-show
set -euo pipefail

ZIP="${1:?Usage: smoke-test.sh <path-to-zip>}"
SMOKE_DIR="$(mktemp -d)"
STARTUP_WAIT=12   # seconds to wait for the process to appear
ALIVE_WAIT=18     # additional seconds for stability verification
LOG_FILE="$SMOKE_DIR/smoke.log"

cleanup() {
    pkill -x LanMessenger 2>/dev/null || true
    sleep 1
    rm -rf "$SMOKE_DIR"
}
trap cleanup EXIT

log() { echo "$1" | tee -a "$LOG_FILE"; }

log "Extracting $ZIP to $SMOKE_DIR..."
unzip -q "$ZIP" -d "$SMOKE_DIR"

APP=$(find "$SMOKE_DIR" -name "*.app" -maxdepth 3 | head -1)
if [ -z "$APP" ]; then
    log "::error::No .app bundle found in archive"
    cp "$LOG_FILE" smoke.log 2>/dev/null || true
    exit 1
fi
log "Found app: $APP"

log "Launching..."
open "$APP" 2>&1 | tee -a "$LOG_FILE" &

log "Waiting ${STARTUP_WAIT}s for process to appear..."
sleep "$STARTUP_WAIT"

PID=$(pgrep -x LanMessenger 2>/dev/null || true)
if [ -z "$PID" ]; then
    log "::error::LanMessenger not running after ${STARTUP_WAIT}s"
    log "Crash reports present:"
    ls ~/Library/Logs/DiagnosticReports/ 2>/dev/null | tee -a "$LOG_FILE" || log "(none)"

    # Embed crash report excerpt if one exists
    CRASH=$(ls ~/Library/Logs/DiagnosticReports/LanMessenger* 2>/dev/null | head -1 || true)
    if [ -n "$CRASH" ]; then
        log "--- Crash report excerpt ---"
        head -80 "$CRASH" | tee -a "$LOG_FILE"
    fi
    cp "$LOG_FILE" smoke.log 2>/dev/null || true
    exit 1
fi

log "✓ Process alive (PID=$PID) — stability check for ${ALIVE_WAIT}s..."
sleep "$ALIVE_WAIT"

if ! kill -0 "$PID" 2>/dev/null; then
    log "::error::LanMessenger (PID=$PID) died during stability window"
    cp "$LOG_FILE" smoke.log 2>/dev/null || true
    exit 1
fi

log "✓ Smoke test passed — app stable for $((STARTUP_WAIT + ALIVE_WAIT))s without crashing"
cp "$LOG_FILE" smoke.log 2>/dev/null || true
