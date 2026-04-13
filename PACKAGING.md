# LAN Messenger Packaging

## Windows

1. Install Python and Inno Setup on a Windows machine.
2. Run:

```powershell
.\windows\build_windows.ps1
```

3. The installer output is written to `dist-installer/LanMessengerSetup.exe`.

What the installer does:
- installs the bundled app into `Program Files`
- creates a Start Menu shortcut
- adds a startup entry in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- optionally creates a Startup folder shortcut
- can be linked from a remote update manifest

## macOS

1. Use a Python environment where `python -m tkinter` works.
2. Run:

```bash
chmod +x macos/build_macos.sh macos/install_mac.sh macos/uninstall_mac.sh
./macos/build_macos.sh
```

3. The build writes:

- `dist/LanMessenger.app`
- `dist-installer/LAN Messenger Installer.app`
- `dist-installer/LAN-Messenger-Installer.dmg`
- `macos/releases/LAN-Messenger-Installer.dmg`

4. Distribute or open `dist-installer/LAN-Messenger-Installer.dmg`.

What the macOS single-file installer does:
- mounts a DMG containing `LAN Messenger Installer.app`
- that installer app copies `LanMessenger.app` into `~/Applications`
- installs `~/Library/LaunchAgents/com.dave.lanmessenger.plist`
- enables launch at login for the current user
- launches the app after install
- can be linked from a remote update manifest

The DMG contains:
- copies `LanMessenger.app` into `~/Applications`
- `LAN Messenger Installer.app`

To remove it:

```bash
./macos/uninstall_mac.sh
```

## Remote Update Server

The app can check a hosted JSON manifest for newer versions.

The static update feed is in:

- `update_server/README.md`
- `update_server/lan-messenger-update.json`
- `update_server/index.html`
- `update_server/build_update_server.py`

Set the update server URL in the app Settings. You can use either:

- the direct manifest URL
- a folder URL that contains `lan-messenger-update.json`

To generate a deployable update feed after building both installers:

```bash
python3 update_server/build_update_server.py --version 1.5.0
```
