# LAN Messenger Native Rewrite — Session Progress Log

This file is the handoff document for future Claude sessions.  
Read this first. Then read `PROTOCOL.md`. Then look at the source.

---

## What This Project Is

Replacing the Python/Tkinter monolith (`main.py`, ~4,400 lines) with two native apps:

- **macOS**: Swift 5.9 + SwiftUI, macOS 13+, Swift Package Manager
- **Windows**: C# 12 + WinUI 3, .NET 8 (not started yet)

Both apps must speak the **exact same wire protocol** as the Python app so all three can coexist on the same LAN during transition. The Python app continues to work throughout.

The full original implementation plan is in the user's conversation history. The implementation plan document is NOT reproduced here — this file tracks what has actually been built and what to do next.

---

## Repository Layout

```
main.py                          ← Python reference app (do not delete)
PROTOCOL.md                      ← Canonical wire format spec (Phase 1)
QA_CHECKLIST.md                  ← 87-case manual test plan (Phase 1)
PROGRESS.md                      ← This file
tools/
  generate_vectors.py            ← Deterministic crypto vector generator
test_vectors/
  known_good_exchange.json       ← Pre-computed Python crypto vectors
lan-messenger-native/
  macos/
    Package.swift                ← SPM package (open in Xcode or swift build)
    LanMessenger/
      App/
        LanMessengerApp.swift    ← @main entry point + placeholder UI
      Core/
        Protocol/
          PacketTypes.swift
          FrameCodec.swift
          PacketValidator.swift
        Crypto/
          KeyManager.swift
          SessionCrypto.swift
          HistoryCrypto.swift
        Networking/
          DiscoveryService.swift
          PeerSession.swift
          NetworkCoordinator.swift
        Persistence/
          ConfigStore.swift
          HistoryStore.swift
          FileTransferStore.swift
        Services/
          MessagingService.swift
          FileTransferService.swift
          NotificationService.swift
          UpdateService.swift
      UI/
        AppModel.swift           ← @MainActor root state, wires all services
    LanMessengerTests/
      PacketValidatorTests.swift
      FrameCodecTests.swift
      CryptoTests.swift
      HistoryStoreTests.swift
      ConfigStoreTests.swift
      known_good_exchange.json   ← copy of test_vectors/ for the test bundle
macos/                           ← OLD Python build scripts (leave alone)
windows/                         ← OLD Python build scripts (leave alone)
update_server/                   ← Update manifest server (leave alone)
```

---

## Phase Status

| Phase | Description | Status |
|---|---|---|
| 1 | PROTOCOL.md + QA checklist + crypto test vectors | **Done** |
| 2 | macOS: SPM package + all Core layers + 40 unit tests | **Done — `swift test` 40/40 passing** |
| 3 | macOS: Full SwiftUI shell (sidebar, chat, composer, settings) | **Done — `swift build` clean, 40/40 tests** |
| 4 | macOS: Polish, accessibility, packaging, migration UI | **Done — `swift build` clean, 40/40 tests** |
| 5 | Windows: VS solution + Core layers + tests | Not started |
| 6 | Windows: WinUI 3 shell | Not started |
| 7 | Windows: Polish, packaging | Not started |
| 8 | Cross-platform integration testing | Not started |

---

## Phase 1 — What Was Done

**Files created:** `PROTOCOL.md`, `QA_CHECKLIST.md`, `tools/generate_vectors.py`, `test_vectors/known_good_exchange.json`

**How to regenerate vectors:**
```bash
pip install cryptography
python tools/generate_vectors.py
```

**PROTOCOL.md** was derived directly from `main.py` source, not from the plan document. It is authoritative. Key facts it captures that are easy to get wrong:

- UDP discovery packets have **no length-prefix framing** (framing is TCP only)
- Discovery reply goes to `{source_ip}:54231` (UDP, back to the discovery port, not TCP)
- History is keyed by **peer IP address**, not public key
- HKDF salt is **empty bytes** (`None` in Python = `Data()` in Swift)
- AES-GCM ciphertext stored/transmitted as `ciphertext ‖ tag` (tag appended, 16 bytes)
- `message_id` and `transfer_id` are `uuid4().hex` — 32 lowercase hex chars, **no dashes**
- History AAD is the literal bytes `b"history-v1"` (10 bytes)
- Frame size check: `size <= 0 or size > 50 * 1024 * 1024` — zero is also rejected
- Filename sanitization: split on `/`, take last component — backslash is NOT a separator on POSIX

---

## Phase 2 — What Was Done

**Built:** Full macOS Core layer in Swift. All 18 source files + 5 test files. `swift build` clean, `swift test` 40/40.

**How to build/test:**
```bash
cd lan-messenger-native/macos
swift build
swift test
```

**How to open in Xcode:** Double-click `lan-messenger-native/macos/Package.swift` or `File → Open → select Package.swift`.

### What each file does

**Core/Protocol/**
- `PacketTypes.swift` — `Codable` structs for all 9 packet types + `ValidatedPacket` enum
- `FrameCodec.swift` — encode/decode 4-byte big-endian length-prefixed frames; `maxFrameSize = 50 MiB`
- `PacketValidator.swift` — pure functions; validate + parse JSON dicts into `ValidatedPacket`; `sanitizeFilename`

**Core/Crypto/**
- `KeyManager.swift` — singleton; generates X25519 key on first launch, stores raw 32-byte private key in Keychain (`service: "com.dave.lanmessenger"`, `account: "privateKey"`); `importFromBase64()` for Python migration
- `SessionCrypto.swift` — `symmetricKey(myPrivate:theirPublicKeyB64:)` via X25519+HKDF; `encrypt/decrypt`; convenience wrappers `encryptForPeer/decryptFromPeer`
- `HistoryCrypto.swift` — `historyKey(privateKey:)` via HKDF with `info="lan-messenger-history"`; `encryptHistory/decryptHistory` → outer `{"nonce":"...","ciphertext":"..."}` JSON

**Core/Networking/**
- `DiscoveryService.swift` — BSD sockets; binds UDP 54231 with `SO_REUSEPORT`; joins multicast `239.255.42.99`; sends beacons every 1.5 s; replies to discovery packets; calls `delegate` on main queue
- `PeerSession.swift` — one persistent TCP connection per peer; `CFStreamCreatePairWithSocketToHost`; exponential back-off reconnect (0.5 s → 2 s → 5 s); `onPacket` delivered on main queue
- `NetworkCoordinator.swift` — owns `DiscoveryService` + all `PeerSession`s; BSD `accept()` loop for inbound TCP; dispatches packets to delegate via `Task { @MainActor in ... }`

**Core/Persistence/**
- `ConfigStore.swift` — singleton; `AppConfig` Codable struct saved to `~/Library/Application Support/LanMessenger/config.json`; `importPythonConfig()` reads `~/.lan_messenger/config.json`, copies `history.enc`, returns raw private key bytes for caller to decide on
- `HistoryStore.swift` — singleton; loads/saves `history.enc` via `HistoryCrypto`; keyed by peer IP; 200-entry cap per peer; `append/updateStatus/markReadReceiptSent`
- `FileTransferStore.swift` — tracks in-progress incoming transfers (temp path = `{inbox}/{transferId}_{filename}.part`); outgoing queue per peer IP; dedup on finalize

**Core/Services/**
- `MessagingService.swift` — `@MainActor`; `sendText` (encrypt + fire TCP + history); `sendTyping` (throttled); `sendReceipt`; `deliverPending` (queued offline messages); handles `text/typing/receipt` packets
- `FileTransferService.swift` — `@MainActor`; `enqueue` → one transfer at a time per peer; `sendFile` runs `Task.detached`; handles `fileStart/fileChunk/fileEnd` packets
- `NotificationService.swift` — wraps `UNUserNotificationCenter`; `showMessage` + `showFileReceived`
- `UpdateService.swift` — fetches manifest JSON, compares `version` string numerically, calls back on main queue

**UI/**
- `AppModel.swift` — `@MainActor ObservableObject`; `@Published peers`, `conversations`, `messages`, `typingStates`, `activeTransfers`; wires all service callbacks; `sendMessage/sendTyping/sendFile/sendReadReceipt`; peer timeout timer (7 s); `checkMigration()` / `acceptMigrationWithExistingKey()` / `acceptMigrationWithFreshKey()`

**App/**
- `LanMessengerApp.swift` — `@main`; `WindowGroup` + `MenuBarExtra`; placeholder `ContentView` (sidebar list + "UI coming in Phase 3"); placeholder `TrayMenuView`

### Compiler bugs fixed during Phase 2 (important for future edits)

1. **`Darwin.bind` disambiguation** — inside classes that inherit from `NSObject`, `bind(_:_:_:)` resolves to an instance method. Always write `Darwin.bind(...)` in networking files.

2. **`InputStream.read` pointer arithmetic** — `stream.read(&buffer + offset, ...)` creates an illegal temporary pointer. Use `buffer.withUnsafeMutableBytes { ptr in stream.read(ptr.baseAddress!.advanced(by: offset).assumingMemoryBound(to: UInt8.self), ...) }`.

3. **`@MainActor` on delegate methods** — mark each protocol method `@MainActor` individually (not the whole protocol). Call them from background threads via `Task { @MainActor [weak self] in ... }`.

4. **`ConfigStore.config` mutability** — `private(set) var config` on a struct prevents sub-property mutation. Use plain `var config`.

5. **Filename sanitizer** — `URL(fileURLWithPath: "")` returns the current directory name (e.g. `"macos"`), not `""`. Use `name.components(separatedBy: "/").last` instead.

6. **Test vector resource** — SPM rejects `.copy("../../../path")` outside the package directory. The vector file is copied into `LanMessengerTests/known_good_exchange.json` and bundled as `.copy("known_good_exchange.json")`.

---

## Phase 3 — What Was Done

Built the full SwiftUI shell. `swift build` clean, 40/40 tests still passing (UI files add no test targets of their own — Core tests are unchanged).

### Files created/replaced

```
LanMessenger/UI/
  Theme.swift                  ← Color palette (dark+light), avatar colors, timestamp formatter
  AvatarView.swift             ← Reusable circle avatar with colored initials
  Sidebar/
    SidebarView.swift          ← List with toolbar (Settings + Contacts), empty-state overlay
    ConversationRowView.swift  ← Avatar + name + last-message preview + timestamp + unread badge
    ContactsView.swift         ← Saved contacts list with swipe-to-delete
  Chat/
    ChatView.swift             ← Header (avatar, name, online dot, typing) + LazyVStack messages + composer
    MessageBubbleView.swift    ← Outgoing (right, green) / incoming (left, white) UnevenRoundedRectangle bubbles
    ComposerView.swift         ← NSViewRepresentable NSTextView, Return=send Shift+Return=newline, drag-drop files
    FileTransferBannerView.swift ← Progress bar + KB/KB label
  Settings/
    SettingsView.swift         ← Username, inbox dir picker, update server, "Check for Updates" button, version
App/
  LanMessengerApp.swift        ← Replaced placeholder; NavigationSplitView + MigrationView sheet + TrayMenuView
```

### Key implementation notes

- **`Theme` enum** holds all adaptive colors as `(ColorScheme) -> Color` functions. Timestamp formatter uses `Date.formatted(.dateTime...)` (macOS 12+ API, fine for our macOS 13+ target).
- **`AvatarView`** computes initials from first two words; picks one of seven deterministic palette colors via `name.hashValue`.
- **`ComposerTextEditor: NSViewRepresentable`** wraps `NSTextView` inside `NSTextView.scrollableTextView()`. `NSTextViewDelegate.textView(_:doCommandBy:)` intercepts `insertNewline:` — Shift held → `insertNewlineIgnoringFieldEditor`, bare Return → calls `onSubmit`. The surrounding `ZStack` with a hidden `Text` mirror drives the auto-grow height from 36 pt to 120 pt.
- **`ChatView`** uses `ScrollViewReader` + `LazyVStack` with `.id(entry.id)`. `scrollToBottom` fires on `.onAppear` (no animation) and on `entries.count` change (animated). Read receipts fire on `.onAppear` and every time the count increases.
- **`MessageBubbleView`** receives `isFirstInRun` from the parent (`entries[i-1].incoming != entries[i].incoming`). First-in-run incoming bubbles show the sender name; their bottom-leading corner is tight (radius 4); subsequent ones are fully rounded.
- **`MigrationView`** is presented as a `.sheet` from `ContentView` driven by `model.showMigrationPrompt`. It calls `model.acceptMigrationWithExistingKey()` or `model.acceptMigrationWithFreshKey()` then dismisses.
- **`NavigationSplitView`** sidebar column hosts `SidebarView`; detail column hosts `ChatView(peerIP:).id(ip)` — the `.id` modifier forces a fresh view when the selected peer changes, resetting scroll position and read-receipt state.
- **`TrayMenuView`** lists conversations; a green `●` appears next to peers with unread messages. `NSApp.activate(ignoringOtherApps: true)` brings the window forward on click.

### Design spec (from the implementation plan)

**Colors (support both light and dark via `.colorScheme`)**
- Sidebar bg: `#111B21` dark / `#F0F2F5` light
- Chat bg: `#0D1418` dark / `#E5DDD5` light
- Outgoing bubble: `#005C4B` dark / `#DCF8C6` light
- Incoming bubble: `#202C33` dark / `#FFFFFF` light
- Accent: `#25D366`

**Sidebar row:** colored-initials avatar, peer name, last message preview, timestamp (right), unread count badge.

**Chat header:** avatar, peer name, online dot, typing indicator (`"{name} is typing…"`).

**Message bubble:**
- Incoming: sender name (first in a run), text, timestamp bottom-left
- Outgoing: text, timestamp + status icon (✓ Sent, ✓✓ Delivered/Read in accent) bottom-right
- File transfer: progress bar + `{n} / {total}` or "Received ✓"

**Composer:** `TextEditor` growing from 36 pt to ~120 pt; send button right; paperclip left; drag-and-drop onto chat area calls `appModel.sendFile(path:toPeerIP:)`.

**Receipts and read tracking:** when user opens a conversation, call `appModel.sendReadReceipt(for:peerIP:)` for each unread incoming message.

### AppModel API the UI calls

```swift
// Send a text message
appModel.sendMessage("Hello", toPeerIP: ip)

// Signal typing
appModel.sendTyping(true, toPeerIP: ip)

// Send a file
appModel.sendFile(path: "/path/to/file.jpg", toPeerIP: ip)

// Mark messages read (call when conversation opens)
for entry in appModel.messages[ip] ?? [] {
    appModel.sendReadReceipt(for: entry, peerIP: ip)
}

// Observe progress
appModel.activeTransfers[ip]   // → (label: String, bytes: Int64, total: Int64)?
appModel.typingStates[ip]      // → (sender: String, active: Bool)?
appModel.conversations         // → [ConversationViewModel]  (sorted by recency)
appModel.messages[ip]          // → [MessageEntry]
```

### Settings sheet

Wire to `ConfigStore.shared.config` directly:
- `username` → calls `coordinator.start(username:)` on change (or restart required)
- `inboxDir` → `ConfigStore.shared.config.inboxDir`
- `updateServerURL` → `ConfigStore.shared.config.updateServerURL`
- "Check for Updates" button → `UpdateService.shared.check(manifestURL:)`
- Migration banner (`appModel.showMigrationPrompt`) → present sheet with "Import existing key" / "Generate fresh key"

---

## Entitlements Needed for Xcode Distribution

When creating the real `.xcodeproj` (or converting SPM to Xcode project):

```xml
<!-- LanMessenger.entitlements -->
<key>com.apple.security.network.client</key>    <!-- TCP outbound -->
<key>com.apple.security.network.server</key>    <!-- TCP inbound listener -->
<key>com.apple.developer.networking.multicast</key>  <!-- requires Apple approval -->
```

`Info.plist` keys:
```xml
<key>NSLocalNetworkUsageDescription</key>
<string>LAN Messenger uses the local network to discover and message nearby peers.</string>
<key>NSUserNotificationsUsageDescription</key>
<string>LAN Messenger shows notifications for incoming messages and file transfers.</string>
<key>LSMinimumSystemVersion</key>
<string>13.0</string>
```

Until the multicast entitlement is approved by Apple, the app falls back to subnet broadcasts (`x.x.x.255`) which already works for single-subnet LANs.

---

## Quick Commands

```bash
# From lan-messenger-native/macos/

# Build
swift build

# Run all tests
swift test

# Run one test class
swift test --filter CryptoTests

# Regenerate test vectors (from repo root)
python tools/generate_vectors.py
```

---

## Windows — Not Started

The C#/WinUI 3 app goes in `lan-messenger-native/windows/`. When starting it:

1. Read `PROTOCOL.md` — the wire format is identical
2. Use `test_vectors/known_good_exchange.json` to verify crypto before live testing
3. X25519 requires BouncyCastle (`Portable.BouncyCastle` NuGet) — .NET 8 built-ins don't expose raw X25519
4. HKDF and AES-GCM use `System.Security.Cryptography` built-ins
5. Private key stored DPAPI-protected: `ProtectedData.Protect(privateKeyBytes, null, DataProtectionScope.CurrentUser)`
6. See the implementation plan in the conversation for the full C# equivalent of each Swift file

---

## Phase 4 — What To Do Next

Polish, accessibility, packaging.

### Suggested work items

**Accessibility**
- Add `.accessibilityLabel` to `AvatarView`, status icons, and unread badge
- `ConversationRowView`: expose unread count via `accessibilityValue`
- `MessageBubbleView`: `accessibilityLabel` = "{sender}: {text}, {time}, {status}"

**Keyboard navigation**
- ⌘N → focus composer (currently no shortcut)
- ⌘W → close window (already works via macOS default)
- Arrow keys in sidebar should already work via `List` selection

**Missing minor features**
- `ContactsView` "Add contact" flow (needs a new sheet with IP + username fields, then `ConfigStore.shared.config.contacts.append(...)`)
- "Hide conversation" context menu on sidebar rows → `ConfigStore.shared.config.hiddenConversations.append(conv.peerIP)`
- Long-press / right-click bubble for copy text
- File bubble in chat showing filename + "Open" button (current System message just shows plain text)

**Packaging (DMG)**
- Convert SPM package to Xcode project: `File → New → Project`, then drag in the SPM package
- Add `LanMessenger.entitlements` (network client/server; multicast pending Apple approval)
- Add `Info.plist` keys: `NSLocalNetworkUsageDescription`, `NSUserNotificationsUsageDescription`, `LSMinimumSystemVersion 13.0`
- Set bundle ID to `com.dave.lanmessenger`
- Create DMG with `create-dmg` (Homebrew): `create-dmg --volname "LAN Messenger" --app-drop-link 660 185 LanMessenger.dmg LanMessenger.app`
- Notarize with `xcrun notarytool`

---

*Last updated: 2026-05-07 after Phase 3 completion.*
