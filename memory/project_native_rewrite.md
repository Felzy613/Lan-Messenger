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

## macOS Bug Fixes Applied (2026-05-20)

- **File transfer freeze**: eliminated per-chunk `DispatchQueue.main.async` hops for byte counting. Byte tracking now lives in `ChunkQueueState` (accessed only from the serial `chunkQueue`). Progress events are coalesced to ~12 Hz; for a 100 MB file this reduces main-thread dispatches from ~1600 to ~96.
- **Unsaved contact reply**: `AppModel` now maintains `knownPeerKeys: [String: String]` (ip → publicKeyB64) populated from every received packet. `sendMessage` and `sendFile` use it as a fallback so replies work even for unsaved/offline contacts.
- **Dock hiding default**: `hideFromDock` default changed from `false` to `true` (hide from dock out of the box). `WindowController.showMainWindow` and `AppModel.showMainWindow` no longer force `.regular` when the user wants to hide from dock.
- **Settings renamed**: "Hide from Dock" toggle renamed to "Don't hide icon in dock" with inverted binding.
- **Offline status**: Contacts page now shows "Offline" instead of the IP address for offline contacts.
- **Typing indicator**: Changed from "\(sender) is typing…" to "typing…" in ChatView and ConversationRowView.
- **Verbose logging**: Added `NetLogger.verbose()` gated by `AppConfig.verboseLogging`. FileTransferService now logs transfer lifecycle, progress, and errors. Settings page has a Logging section with toggle, Open Logs Folder, and Export Log buttons.

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
