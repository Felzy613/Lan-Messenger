---
name: LAN Messenger Native Rewrite
description: Active project to replace Python/Tkinter app with Swift/SwiftUI (macOS) and C#/WinUI 3 (Windows) native apps that keep the existing wire protocol
type: project
---

Goal is two fully native apps — Swift/SwiftUI on macOS 13+ and C#/WinUI 3 on Windows — that maintain wire-protocol compatibility with the Python reference app (v1.5.0) so both generations can interoperate during the transition.

**Why:** Python/Tkinter carries a non-native look, difficult distribution, and scripting-language overhead. Native apps give better UX and simpler packaging.

**How to apply:** Always keep the wire protocol compatible with the Python app. When building native code, verify against `PROTOCOL.md` and `test_vectors/known_good_exchange.json` before doing live testing.

## Current Status (as of 2026-05-07)

Phase 1 — COMPLETE.
Phase 2 — COMPLETE. `swift build` clean, `swift test` 40/40 passing.
Phase 3 — COMPLETE. Full SwiftUI shell built; `swift build` clean, 40/40 tests still passing.

### Phase 1 files (branch `claude/hardcore-ride-3aec16`):
- `PROTOCOL.md` — canonical wire format spec (updated after Phase 2 with backslash sanitization note)
- `QA_CHECKLIST.md` — 13-section manual test plan (87 test cases)
- `tools/generate_vectors.py` — deterministic vector generator
- `test_vectors/known_good_exchange.json` — three vectors: text message, file_chunk, history file

### Phase 2 files (`lan-messenger-native/macos/`):
- `Package.swift` — SPM package (open in Xcode or `swift build` / `swift test`)
- `LanMessenger/App/LanMessengerApp.swift` — @main entry + wired-up ContentView (updated in Phase 3)
- `LanMessenger/Core/Protocol/` — PacketTypes, FrameCodec, PacketValidator
- `LanMessenger/Core/Crypto/` — KeyManager (Keychain), SessionCrypto (CryptoKit), HistoryCrypto
- `LanMessenger/Core/Networking/` — DiscoveryService (UDP), PeerSession (TCP), NetworkCoordinator
- `LanMessenger/Core/Persistence/` — ConfigStore, HistoryStore, FileTransferStore
- `LanMessenger/Core/Services/` — MessagingService, FileTransferService, NotificationService, UpdateService
- `LanMessenger/UI/AppModel.swift` — @MainActor root state, wires all services
- `LanMessengerTests/` — 40 unit tests across 5 test classes (all green)

### Phase 3 files added (`lan-messenger-native/macos/LanMessenger/UI/`):
- `Theme.swift` — color palette (dark+light adaptive), avatar palette, timestamp formatter
- `AvatarView.swift` — reusable colored-initials circle
- `Sidebar/SidebarView.swift` — sidebar list with Settings/Contacts toolbar buttons, empty-state overlay
- `Sidebar/ConversationRowView.swift` — avatar + name + preview + timestamp + unread badge
- `Sidebar/ContactsView.swift` — saved contacts list with swipe-to-delete
- `Chat/ChatView.swift` — header (avatar, name, online dot, typing) + ScrollViewReader message list + composer
- `Chat/MessageBubbleView.swift` — UnevenRoundedRectangle bubbles, incoming left / outgoing right
- `Chat/ComposerView.swift` — NSViewRepresentable NSTextView (Return=send, Shift+Return=newline), drag-drop files, auto-grow 36→120 pt
- `Chat/FileTransferBannerView.swift` — progress bar + KB/KB label
- `Settings/SettingsView.swift` — username, inbox dir picker, update server, "Check for Updates", version
- `App/LanMessengerApp.swift` — NavigationSplitView + MigrationView sheet + TrayMenuView (tray icon with unread indicator)

Next: Phase 4 — Polish, accessibility, packaging (DMG + notarization)

## Implementation Plan Phases

| Phase | Description | Status |
|---|---|---|
| 1 | PROTOCOL.md + QA checklist + test vectors | Done |
| 2 | macOS: Xcode project + Crypto/Protocol/Networking + tests | Done (SPM, 40 tests passing) |
| 3 | macOS: SwiftUI shell (sidebar, chat, composer) | Done (swift build clean) |
| 4 | macOS: Polish, accessibility, packaging | Next |
| 5 | Windows: VS solution + Crypto/Protocol/Networking + tests | Not started |
| 6 | Windows: WinUI 3 shell | Not started |
| 7 | Windows: File transfer + notifications + tray + settings + update | Not started |
| 8 | Cross-platform integration testing | Not started |
| 9 | Polish, packaging, release | Not started |

## Key Protocol Facts (verified from main.py source)

- UDP discovery port: 54231; TCP message port: 54232
- Multicast group: 239.255.42.99, TTL=1
- Discovery interval: 1.5 s; peer timeout: 7 s
- Frame: 4-byte big-endian uint32 length prefix + UTF-8 JSON (no frame on UDP)
- Max frame size: 50 MiB (size <= 0 or > 50*1024*1024 → close)
- Discovery payload: type, username, port, public_key_b64, ips (raw UDP JSON, no framing)
- Discovery reply sent to {source_ip}:54231 (UDP, not TCP)
- Text AAD: message_id.encode("utf-8") (32 hex chars)
- File chunk AAD: transfer_id.encode("utf-8") (32 hex chars)
- History AAD: b"history-v1"
- History keyed by peer IP address (not public key)
- History key: HKDF(ikm=raw_private_key_bytes, info=b"lan-messenger-history")
- Session key: HKDF(ikm=X25519_shared_secret, info=b"lan-messenger")
- AES-GCM: 12-byte nonce, 16-byte tag appended to ciphertext
- message_id / transfer_id: uuid4().hex (32 hex chars, no dashes)
- sanitize_filename: Path(name).name.strip() or "file", remove null bytes
- Private key stored insecurely in Python's config.json (migration path needed)
