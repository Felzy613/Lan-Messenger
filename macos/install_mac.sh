#!/bin/zsh
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 /path/to/LanMessenger.app"
  exit 1
fi

APP_SOURCE="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
APP_TARGET="/Applications/LanMessenger.app"
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"
PLIST_TARGET="$LAUNCH_AGENTS_DIR/com.dave.lanmessenger.plist"

mkdir -p "$LAUNCH_AGENTS_DIR"
rm -rf "$APP_TARGET"
cp -R "$APP_SOURCE" "$APP_TARGET"

sed "s|@APP_PATH@|$APP_TARGET|g" "$(dirname "$0")/com.dave.lanmessenger.plist.template" > "$PLIST_TARGET"

launchctl unload "$PLIST_TARGET" >/dev/null 2>&1 || true
launchctl load "$PLIST_TARGET"

echo "Installed $APP_TARGET"
echo "Startup enabled with $PLIST_TARGET"
