#!/bin/zsh
set -euo pipefail

PLIST_TARGET="$HOME/Library/LaunchAgents/com.dave.lanmessenger.plist"
APP_TARGET="/Applications/LanMessenger.app"

launchctl unload "$PLIST_TARGET" >/dev/null 2>&1 || true
rm -f "$PLIST_TARGET"
rm -rf "$APP_TARGET"

echo "Removed LAN Messenger and disabled startup."
