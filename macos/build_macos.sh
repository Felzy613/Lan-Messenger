#!/bin/zsh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

PYTHON_BIN="${PYTHON_BIN:-}"
if [[ -z "$PYTHON_BIN" ]]; then
  if [[ -x "/opt/homebrew/bin/python3" ]]; then
    PYTHON_BIN="/opt/homebrew/bin/python3"
  else
    PYTHON_BIN="python3"
  fi
fi

if [[ ! -d ".venv" ]]; then
  "$PYTHON_BIN" -m venv .venv
fi

source .venv/bin/activate
python -m pip install --upgrade pip
python -m pip install -r requirements.txt
pyinstaller --clean --noconfirm LanMessenger.spec

INSTALLER_APP="$ROOT/dist-installer/LAN Messenger Installer.app"
CONTENTS_DIR="$INSTALLER_APP/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"
rm -rf "$INSTALLER_APP"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"
cp -R "$ROOT/dist/LanMessenger.app" "$RESOURCES_DIR/LanMessenger.app"
cp "$ROOT/com.dave.lanmessenger.plist.template" "$RESOURCES_DIR/com.dave.lanmessenger.plist.template"
cp "$ROOT/assets/LanMessenger.icns" "$RESOURCES_DIR/LanMessenger.icns"
cp "$ROOT/installer_launcher.sh" "$MACOS_DIR/lanmessenger-installer"
chmod +x "$MACOS_DIR/lanmessenger-installer"
cp "$ROOT/InstallerInfo.plist" "$CONTENTS_DIR/Info.plist"

STAGING_DIR="$ROOT/dist-installer/dmg-staging"
DMG_PATH="$ROOT/dist-installer/LAN-Messenger-Installer.dmg"
RELEASES_DIR="$ROOT/releases"
rm -rf "$STAGING_DIR" "$DMG_PATH"
mkdir -p "$STAGING_DIR"
cp -R "$INSTALLER_APP" "$STAGING_DIR/"

hdiutil create \
  -volname "LAN Messenger Installer" \
  -srcfolder "$STAGING_DIR" \
  -ov \
  -format UDZO \
  "$DMG_PATH"

rm -rf "$STAGING_DIR"
mkdir -p "$RELEASES_DIR"
cp "$DMG_PATH" "$RELEASES_DIR/"
rm -rf "$ROOT/build" "$ROOT/dist" "$ROOT/dist-installer" "$ROOT/__pycache__"

echo
echo "Build complete."
echo "Release installer: releases/LAN-Messenger-Installer.dmg"
echo "Temporary build artifacts removed."
