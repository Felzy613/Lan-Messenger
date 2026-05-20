---
name: LAN Messenger Native Rewrite
description: Current status for the native Swift/SwiftUI macOS app and C#/WinUI 3 Windows app
type: project
---

Goal: maintain two native LAN Messenger apps that interoperate over the same
peer-to-peer wire protocol. The Python/Tkinter app is no longer present in this
checkout as an active source tree, but compatibility decisions still preserve the
legacy protocol where needed.

## Current Status

- macOS native app lives in `src/macos/`.
- Windows native app lives in `src/windows-native/`.
- Protocol spec lives in `PROTOCOL.md`.
- High-detail docs live in `docs/`.
- macOS test suite currently has 52 Swift test methods.
- Windows test suite currently has 45 MSTest methods.

## Completed Native Capabilities

- UDP LAN discovery with per-interface broadcast/multicast/unicast behavior.
- TCP framed messaging and file-transfer packets.
- X25519/HKDF/AES-GCM text and file chunk encryption.
- Encrypted local history keyed by peer IP and capped to 200 entries.
- Secure private key storage: Keychain on macOS, DPAPI on Windows.
- Saved contacts, hidden/deleted conversations, archive/unarchive, contact photos.
- Typing indicators, sent/read receipts, and rank-aware message status.
- Reply metadata as optional native fields.
- Offline pending text and file queues.
- macOS menu-bar lifecycle and Windows tray lifecycle.
- GitHub Releases update checks and platform-specific installers.
- CI pipelines for platform tests, packaging, smoke tests, releases, and integrity.

## Current Architecture

Both platforms mirror these layers:

```
Core/Protocol
Core/Crypto
Core/Networking
Core/Persistence
Core/Services
UI
```

`AppModel` on each platform wires the services into UI state and owns peer,
conversation, pending queue, read receipt, contact, and update behavior.

## Key Commands

macOS:

```
cd src/macos
swift build
swift test
```

Windows:

```
cd src\windows-native
msbuild /t:Restore /p:Configuration=Release /p:Platform=x64 LanMessenger.sln
msbuild LanMessenger.Tests\LanMessenger.Tests.csproj /p:Configuration=Release /p:Platform=x64
dotnet vstest <resolved test dll>
```

## Next Work Themes

- Keep docs synchronized with source behavior.
- Preserve protocol compatibility across platforms.
- Run both test suites for protocol/crypto/framing changes.
- Verify UI/runtime changes on the target OS, not only through unit tests.
