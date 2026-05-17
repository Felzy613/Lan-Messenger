# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LAN Messenger is a peer-to-peer local-network chat app with two native implementations:

- **macOS**: Swift/SwiftUI, SPM package at `src/macos/`
- **Windows**: C#/WinUI 3, VS solution at `src/windows-native/`

Both must remain wire-protocol compatible so they can interoperate. The authoritative protocol spec is `PROTOCOL.md`.

## Build & Test Commands

### macOS (Swift/SPM)

```bash
cd "src/macos"
swift build          # compile
swift test           # run all 40 unit tests
```

The Xcode project (`LanMessenger.xcodeproj`) also works; SPM and Xcode share the same sources. Target: macOS 13+.

### Windows (C#/WinUI 3)

Must be run on a Windows machine with Visual Studio 2022 + Windows App SDK 1.5:

```powershell
cd src\windows-native
dotnet build LanMessenger.sln          # compile
dotnet test LanMessenger.Tests         # run unit tests
```

Target: .NET 8, Windows 10 (19041+), x64 only.

## Repository Layout

```
PROTOCOL.md                   # Authoritative wire-protocol spec — read this first
src/
  macos/                      # Swift/SPM native app (Phases 2–3 complete)
    Package.swift
    LanMessenger/
      App/                    # @main entry, NavigationSplitView, tray
      Core/
        Protocol/             # PacketTypes, FrameCodec, PacketValidator
        Crypto/               # KeyManager (Keychain), SessionCrypto, HistoryCrypto
        Networking/           # DiscoveryService (UDP), PeerSession (TCP), NetworkCoordinator
        Persistence/          # ConfigStore, HistoryStore, FileTransferStore
        Services/             # MessagingService, FileTransferService, NotificationService, UpdateService
      UI/                     # AppModel (@MainActor root state), Theme, AvatarView, Sidebar/, Chat/, Settings/
    LanMessengerTests/        # 40 unit tests (keep green)
      known_good_exchange.json  # Must live here (not in repo root) — SPM rejects resources outside package root
  windows-native/             # C#/WinUI 3 native app (Phase 5–7 in progress)
    LanMessenger/             # Same Core/ + UI/ structure as macOS
    LanMessenger.Tests/       # MSTest unit tests
      known_good_exchange.json
```

## Wire Protocol — Critical Facts

Read `PROTOCOL.md` before touching any networking or crypto code. Key non-obvious points:

**Transport**
- UDP port 54231 for discovery — **no length-prefix framing** on UDP packets (raw JSON only)
- TCP port 54232 for messages — 4-byte big-endian uint32 length prefix + UTF-8 JSON body
- Max frame size check is `size <= 0 OR size > 50 MiB` — both ends must be rejected
- Discovery replies go back to `{source_ip}:54231` (UDP), not the TCP port

**Cryptography**
- Keys: X25519 key exchange; HKDF-SHA256 with **empty salt** (`b""` / `Data()`) to derive a 32-byte AES-256-GCM symmetric key
- Session key info string: `b"lan-messenger"`; history key info string: `b"lan-messenger-history"`
- AES-GCM: 12-byte nonce, 16-byte tag **appended** to ciphertext before base64 encoding (CryptoKit returns these separately — concatenate manually)
- Message AAD: `message_id.encode("utf-8")`; file chunk AAD: `transfer_id.encode("utf-8")`; history AAD: `b"history-v1"`
- Private key storage: Keychain on macOS (`com.dave.lanmessenger` / `privateKey`), DPAPI on Windows (`%APPDATA%\LanMessenger\private.key.dpapi`) — never plain JSON

**IDs & History**
- `message_id` and `transfer_id` are `uuid4().hex` — 32 lowercase hex chars, **no dashes**
- History is keyed by **peer IP address** (not public key); stored at `~/Library/Application Support/LanMessenger/history.enc` (macOS) or `%APPDATA%\LanMessenger\history.enc` (Windows); capped at 200 messages per conversation

**File Transfer**
- Each transfer uses a **separate TCP connection** (not the persistent peer session)
- Temp file path: `{inbox_dir}/{transfer_id}_{filename}.part`; rename on `file_end`; deduplicate as `{stem}_1{suffix}`, `{stem}_2{suffix}`, ... up to 999, then random 8-hex suffix

## Known Swift Build Gotchas (`src/macos/`)

- `bind()` inside classes extending `NSObject`: use `Darwin.bind(...)` to avoid collision with `NSObject.bind(_:_:_:)`
- `InputStream.read` with pointer offset: use `buffer.withUnsafeMutableBytes { ptr in stream.read(ptr.baseAddress!.advanced(by: offset)..., maxLength: n) }` — pointer arithmetic on the buffer directly is illegal
- `@MainActor` on delegate protocols causes errors at non-isolated call sites; annotate each method individually and dispatch from background threads via `Task { @MainActor [weak self] in ... }`
- `ConfigStore.config` must be `var` (not `private(set) var`) — `MessagingService` mutates sub-properties on the struct value type directly
- Filename sanitization: use `name.components(separatedBy: "/").last ?? ""` not `URL(fileURLWithPath:)` (URL returns CWD for empty string); strip null bytes **before** passing through URL

## Version Management

Every commit that touches `src/macos/` or `src/windows-native/` automatically bumps the patch version (e.g. `1.3.1 → 1.3.2`) via the pre-commit hook at `scripts/hooks/pre-commit`. The hook is **not** tracked by git — new contributors must install it manually:

```bash
cp scripts/hooks/pre-commit .git/hooks/pre-commit && chmod +x .git/hooks/pre-commit
```

**Version sources of truth:**

| Platform | JSON (primary) | Synced to |
|----------|---------------|-----------|
| macOS | `version/macos.json` | `src/macos/project.yml` (`MARKETING_VERSION` — MAJOR.MINOR only) |
| Windows | `version/windows.json` | `src/windows-native/LanMessenger/LanMessenger.csproj` (`<Version>`) |

The hook bumps the last numeric segment only. To bump minor or major, edit the relevant JSON manually before staging — the hook will skip re-bumping files staged from `version/` directly.

## Test Vector Verification

Before live-device testing, verify crypto against `known_good_exchange.json` (three vectors: text message, file_chunk, history file). These vectors are the ground truth for cross-platform interoperability. The macOS tests load this file from `LanMessengerTests/known_good_exchange.json`; the Windows tests load from `LanMessenger.Tests/known_good_exchange.json`.
