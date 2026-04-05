#!/bin/zsh
set -euo pipefail

BUNDLE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PAYLOAD_APP="$BUNDLE_DIR/Resources/LanMessenger.app"
TARGET_APP="$HOME/Applications/LanMessenger.app"
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"
PLIST_TARGET="$LAUNCH_AGENTS_DIR/com.dave.lanmessenger.plist"
PLIST_TEMPLATE="$BUNDLE_DIR/Resources/com.dave.lanmessenger.plist.template"

mkdir -p "$HOME/Applications" "$LAUNCH_AGENTS_DIR"
rm -rf "$TARGET_APP"
cp -R "$PAYLOAD_APP" "$TARGET_APP"

sed "s|@APP_PATH@|$TARGET_APP|g" "$PLIST_TEMPLATE" > "$PLIST_TARGET"

launchctl unload "$PLIST_TARGET" >/dev/null 2>&1 || true
launchctl load "$PLIST_TARGET"

/usr/bin/osascript <<OSA
display dialog "LAN Messenger was installed to Home Applications and set to launch at login." buttons {"Open App", "OK"} default button "Open App" with title "LAN Messenger Installer"
OSA

open -a "$TARGET_APP"
