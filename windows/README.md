# Windows Build Instructions

This folder is self-contained for Windows builds.

## Requirements

- Windows
- Python 3
- Inno Setup 6

## Build

Open PowerShell in this `windows/` folder and run:

```powershell
.\build_windows.ps1
```

If PowerShell blocks the script, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build_windows.ps1
```

## What It Does

- creates `.venv` if needed
- installs dependencies from `requirements.txt`
- builds the packaged app with PyInstaller
- builds the installer with Inno Setup

## Output

- `dist-installer\LanMessengerSetup.exe`
