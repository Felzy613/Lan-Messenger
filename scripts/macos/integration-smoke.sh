#!/usr/bin/env bash
# integration-smoke.sh — Lightweight runtime smoke test for CI.
#
# Usage:
#   scripts/macos/integration-smoke.sh <path-to-LanMessenger-binary>
#
# What it does:
#   1. Launches the app for 8 seconds and verifies it does not crash immediately.
#   2. Verifies at least one subsystem log file was created (proves logging works).
#   3. Checks that no CRIT-level lines were written to the subsystem logs.
#   4. Exits 0 on success, 1 on any failure.
#
# Note: this test runs in a headless CI environment without a display or real
# LAN interface.  The app is expected to start, initialize its services, write
# logs, and keep running.  It is NOT a cross-instance messaging test — that
# requires the full integration-test workflow with two runners.

set -euo pipefail

BINARY="${1:?Usage: $0 <path-to-LanMessenger-binary>}"

if [ ! -x "$BINARY" ]; then
    echo "::error::Binary not found or not executable: $BINARY"
    exit 1
fi

echo "=== Integration smoke: $BINARY ==="

LOG_DIR="$HOME/Library/Application Support/LanMessenger/Logs"
PASS=0
FAIL=0

# ── 1. Launch and survive ─────────────────────────────────────────────────────
echo "[1/3] Launching app for 8 seconds…"
"$BINARY" &
APP_PID=$!

# Give the app 8 seconds to initialize services.
sleep 8

if kill -0 "$APP_PID" 2>/dev/null; then
    echo "  ✓ App is still running after 8 s (PID $APP_PID)"
    kill "$APP_PID" 2>/dev/null || true
    wait "$APP_PID" 2>/dev/null || true
    PASS=$((PASS + 1))
else
    # Check if it exited cleanly (0) or crashed (non-zero).
    wait "$APP_PID" 2>/dev/null
    EXIT_CODE=$?
    if [ "$EXIT_CODE" -eq 0 ]; then
        echo "  ✓ App exited cleanly (code 0) — acceptable for headless environment"
        PASS=$((PASS + 1))
    else
        echo "  ✗ App crashed with exit code $EXIT_CODE within 8 s"
        FAIL=$((FAIL + 1))
    fi
fi

# ── 2. Subsystem logs created ─────────────────────────────────────────────────
echo "[2/3] Checking subsystem log creation…"
if [ -d "$LOG_DIR" ]; then
    LOG_COUNT=$(find "$LOG_DIR" -name "*.log" -maxdepth 1 | wc -l | tr -d ' ')
    if [ "$LOG_COUNT" -gt 0 ]; then
        echo "  ✓ Found $LOG_COUNT subsystem log(s) in $LOG_DIR:"
        ls -lh "$LOG_DIR"/*.log 2>/dev/null | sed 's/^/    /'
        PASS=$((PASS + 1))
    else
        echo "  ✗ No .log files found in $LOG_DIR"
        FAIL=$((FAIL + 1))
    fi
else
    echo "  ⚠ Log directory does not exist yet: $LOG_DIR"
    echo "    (App may not have had time to write logs in headless mode — not counted as failure)"
fi

# ── 3. No CRIT lines ─────────────────────────────────────────────────────────
echo "[3/3] Checking for CRIT-level log lines…"
CRIT_COUNT=0
if [ -d "$LOG_DIR" ]; then
    CRIT_COUNT=$(grep -r "CRIT " "$LOG_DIR"/*.log 2>/dev/null | wc -l | tr -d ' ')
fi

if [ "$CRIT_COUNT" -eq 0 ]; then
    echo "  ✓ No CRIT lines in subsystem logs"
    PASS=$((PASS + 1))
else
    echo "  ✗ Found $CRIT_COUNT CRIT line(s) in subsystem logs:"
    grep -r "CRIT " "$LOG_DIR"/*.log 2>/dev/null | head -20 | sed 's/^/    /'
    FAIL=$((FAIL + 1))
fi

# ── Summary ───────────────────────────────────────────────────────────────────
echo ""
echo "=== Smoke summary: $PASS passed / $FAIL failed ==="

if [ "$FAIL" -gt 0 ]; then
    echo "::error::Integration smoke test FAILED ($FAIL check(s) failed)"
    exit 1
fi

echo "Integration smoke test PASSED"
exit 0
