# macOS Build Instructions

This folder is self-contained for macOS builds.

## Requirements

- macOS
- Python 3
- A working Tk-enabled Python environment

## Build

Open Terminal in this `macos/` folder and run:

```bash
chmod +x build_macos.sh
./build_macos.sh
```

## What It Does

- creates `.venv` if needed
- installs dependencies from `requirements.txt`
- builds `LanMessenger.app`
- builds the installer app
- builds the single-file installer DMG
- writes the final installer to `releases/LAN-Messenger-Installer.dmg`
- removes temporary build artifacts

## Output

- `releases/LAN-Messenger-Installer.dmg`
