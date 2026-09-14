---
name: Repo file layout
description: Current LAN Messenger repo layout after the native macOS and Windows rewrite
type: project
---

The current repo is organized around two native apps plus shared docs, scripts,
versions, and CI. The older notes that referenced `lan-messenger-native/`,
root-level `main.py`, PyInstaller specs, `test_vectors/`, or `QA_CHECKLIST.md`
are stale for this checkout.

## Root

```
README.md
CLAUDE.md
PROTOCOL.md
docs/
memory/
scripts/
spikes/            # throwaway platform diagnostics, not part of either app
version/
src/
```

## Documentation

```
docs/
  ARCHITECTURE.md
  DEVELOPMENT.md
  FILE_MAP.md
  REMOTE_DESKTOP.md
  RELEASE_AND_OPERATIONS.md
```

`PROTOCOL.md` is the protocol and persistence source of truth. `CLAUDE.md` is
the operational guide for agents/developers. `docs/FILE_MAP.md` has the
file-by-file inventory. `docs/REMOTE_DESKTOP.md` is the status and handoff
document for the one unreleased feature.

The four shared test fixtures must stay byte-identical across the two test
directories; changing one is a change to both.

## macOS Native App

```
src/macos/
  Package.swift
  project.yml
  VERSION                         # legacy marker only
  scripts/
    build_app.sh
    build_dmg.sh
    generate_icon.py
  LanMessenger/
    App/
    Core/
      Protocol/
      Crypto/
      Networking/
        Media/                    # remote desktop (unreleased)
      Persistence/
      Services/
    UI/
    Info.plist
    LanMessenger.entitlements
    Assets.xcassets/
  LanMessengerTests/
    known_good_exchange.json
    remote_handshake_vector.json
    media_frame_vector.json
    windows_h264_sample.h264      # real Windows encoder output; not regenerable here
```

SwiftPM (`Package.swift`) is the fast dev/test path. `project.yml` generates the
Xcode project used by packaging; generated `LanMessenger.xcodeproj` is ignored.

## Windows Native App

```
src/windows-native/
  LanMessenger.sln
  LanMessenger.iss
  VERSION                         # legacy marker only
  LanMessenger/
    LanMessenger.csproj
    App.xaml
    MainWindow.xaml
    Core/
      Protocol/
      Crypto/
      Networking/
        Media/                    # remote desktop (unreleased)
      Persistence/
      Services/
    UI/
    Assets/
  LanMessenger.Tests/
    LanMessenger.Tests.csproj
    known_good_exchange.json
    remote_handshake_vector.json
    media_frame_vector.json
    windows_h264_sample.h264
```

Use Visual Studio MSBuild for WinUI builds and tests. Inno Setup builds the
installer.

## Scripts And CI

```
scripts/hooks/pre-commit
scripts/macos/package.sh
scripts/macos/validate-bundle.sh
scripts/macos/validate-dmg.sh
scripts/macos/smoke-test.sh
scripts/windows/smoke-test.ps1
scripts/shared/close-ci-issues.sh

.github/workflows/
.github/actions/
```

## Version Sources

```
version/macos.json
version/windows.json
```

These are the canonical release versions. `src/macos/VERSION` and
`src/windows-native/VERSION` are legacy markers and are not CI sources of truth.
