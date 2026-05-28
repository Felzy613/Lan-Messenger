# Architecture

This document explains how LAN Messenger works end to end: platform structure,
runtime lifecycle, networking, encryption, persistence, UI state, updates,
packaging, and the key failure modes developers need to keep in mind.

## System Overview

LAN Messenger is a local-network peer-to-peer chat system. Every app instance is
both a discovery broadcaster/listener and a TCP server/client. There is no central
server in the messaging path.

```text
+------------------+          UDP 54231           +------------------+
| macOS app         | <--------------------------> | Windows app      |
| Swift/SwiftUI     |       raw discovery JSON     | C#/WinUI 3       |
+------------------+                              +------------------+
          |                                                   |
          |                  TCP 54232                        |
          +<------------------------------------------------->+
                     framed JSON + encrypted payloads
```

The two implementations are intentionally parallel:

- `Core/Protocol` defines packet types, frame codec, and validation.
- `Core/Crypto` owns X25519, HKDF, AES-GCM, and key storage.
- `Core/Networking` owns interface monitoring, UDP discovery, TCP listener, and
  peer sessions.
- `Core/Persistence` owns config, encrypted history, pending queues, and transfer
  state.
- `Core/Services` owns messaging, file transfer, notifications, updates, and
  diagnostics.
- `UI` owns view models and native views.

## Primary Design Constraints

- Discovery must work on multi-interface machines.
- Protocol fields must remain stable across platforms.
- UDP discovery must remain unframed.
- TCP payloads must remain length-prefixed JSON.
- Message and file content must remain encrypted end to end.
- Private keys must never live in config JSON.
- Local history must remain decryptable across app upgrades.
- UI must stay responsive during large file transfers.
- Packaging and update behavior must be testable in CI.

## Platform Matrix

| Concern | macOS | Windows |
|---|---|---|
| Language/runtime | Swift 5.9 | C#/.NET 8 |
| UI | SwiftUI | WinUI 3 |
| App lifecycle | SwiftUI `App`, `Window`, `MenuBarExtra`, AppKit delegate | `Application`, `MainWindow`, H.NotifyIcon tray |
| Build for dev | SwiftPM | Visual Studio MSBuild |
| App packaging | XcodeGen + xcodebuild + shell packaging | MSBuild publish + Inno Setup |
| Private key store | Keychain | DPAPI |
| Notifications | UserNotifications | Windows App Notifications |
| Launch at login | `SMAppService.mainApp` | Installer/registry/tray preferences |
| Update install | Download ZIP, verify, helper script replaces `.app` | Download EXE, verify, silent elevated Inno installer |
| Logs | Application Support logs plus os_log | `%APPDATA%\LanMessenger\Logs` plus debugger |

## Source Tree

```text
src/macos/
  Package.swift
  project.yml
  LanMessenger/
    App/
    Core/
      Protocol/
      Crypto/
      Networking/
      Persistence/
      Services/
    UI/
  LanMessengerTests/

src/windows-native/
  LanMessenger.sln
  LanMessenger/
    Core/
    UI/
  LanMessenger.Tests/
  LanMessenger.iss
```

Use [FILE_MAP.md](FILE_MAP.md) for the detailed inventory.

## Runtime Lifecycle

### macOS

1. `LanMessengerApp` creates `AppModel` as a `@StateObject`.
2. `LanMessengerAppDelegate.applicationWillFinishLaunching` applies the
   persisted dock policy before SwiftUI creates windows.
3. `AppModel.init` wires service delegates and calls `start`.
4. `start` sets a non-default username from `NSFullUserName`, starts networking,
   requests notification permission, loads history, starts timers, checks legacy
   migration, applies dock/login-item policy, and schedules update checks.
5. The main `Window` hosts `ContentView`, which contains `NavigationSplitView`
   with `SidebarView` and `ChatView`.
6. `MenuBarExtra` remains available after the main window closes and can reopen
   the app.

### Windows

1. `App.OnLaunched` creates and activates `MainWindow`.
2. `MainWindow` creates `AppModel` with the current `DispatcherQueue`.
3. `AppModel.Start` logs crypto runtime diagnostics, sets a non-default username
   from `Environment.UserName`, starts networking, registers notifications, loads
   history, starts timers, checks migration, and schedules update checks.
4. `MainWindow` owns sidebar, content frame, contacts/settings dialogs, archived
   page, and tray commands.
5. `AppWindow.Closing` hides to tray when `close_to_tray` is enabled unless the
   user explicitly quits.

## Core Data Flow

```text
DiscoveryService
  -> NetworkCoordinator
  -> AppModel.upsertPeer
  -> contacts/history migration
  -> refresh sidebar conversations

TCP listener / PeerSession
  -> FrameCodec
  -> PacketValidator
  -> NetworkCoordinator callbacks
  -> MessagingService or FileTransferService
  -> HistoryStore / FileTransferStore
  -> AppModel published state
  -> native UI
```

## Network Architecture

### Interface Monitoring

`NetworkInterfaceMonitor` publishes eligible IPv4 adapters.

Eligibility:

- interface is up;
- not loopback;
- has IPv4 unicast address;
- not APIPA `169.254/16`;
- not `0.0.0.0`.

macOS uses `NWPathMonitor` plus a 5-second polling safety net and `getifaddrs`.
Windows uses `NetworkChange` events plus a 5-second polling safety net and
`NetworkInterface.GetAllNetworkInterfaces`.

The monitor intentionally tracks LAN availability, not internet availability.

### Discovery Service

Discovery owns:

- one receive socket bound to `0.0.0.0:54231`;
- one send socket per eligible interface;
- multicast membership joined per interface;
- a 1.5-second beacon timer.

Every beacon includes the current username, TCP port, public key, and local IPs.
The username is read fresh from config on each beacon so Settings changes do not
require restarting networking.

The service sends to:

- directed subnet broadcast;
- multicast group;
- limited broadcast;
- extra unicast targets.

Windows additionally disables UDP connection reset behavior with
`SIO_UDP_CONNRESET`.

### Network Coordinator

`NetworkCoordinator` owns the high-level network lifecycle:

- starts/stops `NetworkInterfaceMonitor`;
- starts/stops `DiscoveryService`;
- listens on TCP `54232`;
- validates inbound TCP frames;
- exposes packet/discovery callbacks to `AppModel`;
- optionally manages persistent `PeerSession` instances.

Most current message/file sends use one-shot TCP connections for protocol
simplicity. `PeerSession` remains available for persistent queued frame sending.

### Peer Sessions

`PeerSession` is one persistent connection to one peer IP/port. It reconnects
with backoff and serializes outgoing frames. It is not the only send path; services
also fire one-shot TCP frames.

## Protocol Architecture

`Core/Protocol` is intentionally small and mirrored:

- `PacketTypes` contains Codable/JsonSerializer packet definitions.
- `PacketValidator` converts untrusted JSON into validated packet objects.
- `FrameCodec` handles TCP frame encode/decode.

Protocol invariants are documented in [../PROTOCOL.md](../PROTOCOL.md).

Critical boundaries:

- UDP datagrams are raw JSON.
- TCP packets are framed JSON.
- discovery uses `public_key_b64`;
- TCP packets use `sender_public_key_b64`;
- reply metadata is optional;
- invalid input is dropped rather than repaired.

## Cryptography Architecture

### Identity Keys

Each installation generates one X25519 identity keypair.

- Public key is advertised in discovery and packet metadata.
- Private key stays local and protected by platform secure storage.

`KeyManager` loads or creates the key at startup and exposes public key bytes and
base64 for other services.

### Session Encryption

`SessionCrypto` derives a symmetric key per peer using:

```text
X25519(my_private, peer_public)
HKDF-SHA256(empty salt, info="lan-messenger", length=32)
AES-256-GCM
```

The AES-GCM tag is appended to ciphertext before base64 encoding so Swift,
Windows, and legacy Python agree on byte layout.

### History Encryption

`HistoryCrypto` derives an encryption key from the raw local private key:

```text
HKDF-SHA256(empty salt, info="lan-messenger-history", length=32)
AAD = "history-v1"
```

The history file is local-only; it is not sent across the network.

## Persistence Architecture

### ConfigStore

Config is JSON under the platform app-data directory. It stores:

- username;
- saved contacts;
- hidden and archived conversation IPs;
- pending messages;
- pending files;
- inbox path;
- update source;
- platform preferences such as dock/tray/login behavior.

It does not store private keys.

### HistoryStore

`HistoryStore` loads and saves encrypted history through `HistoryCrypto`.

History shape:

```text
peer IP -> list of MessageEntry
```

This IP-keyed model is a compatibility constraint. `AppModel` compensates by
migrating history when a saved contact reappears with the same public key at a
new IP.

The message status update path is rank-aware. This prevents race conditions where
a late local "Sent" update overwrites a remote `sent_receipt` or `read_receipt`.

### FileTransferStore

The store tracks:

- in-progress incoming transfers by `(sender IP, transfer_id)`;
- per-peer outgoing file queues;
- active outgoing peer set.

Incoming transfers write to `.part` files until `file_end`, then rename to a
deduplicated final path.

## Messaging Service

`MessagingService` handles text, typing, receipts, pending message retry, and
cloud relay dispatch.

Send flow:

1. Create `message_id`.
2. Append outgoing history entry with `Sending`.
3. Encrypt text with `message_id` AAD.
4. Build `text` packet with optional reply metadata.
5. Send a one-shot TCP frame.
6. Mark `Sent` or queue pending and mark `Queued`.
7. If the peer's `relay_id_hash` is known and the send failed, **also POST the
   ciphertext to the cloud relay Worker** so delivery can proceed even if this
   device goes offline before the peer reconnects.

Receive flow:

1. Decrypt with `message_id` AAD.
2. Append incoming history entry.
3. Clear typing state.
4. Send `sent_receipt`.

Read flow:

1. `AppModel.markConversationRead` sends `read_receipt` for incoming unread
   messages.
2. It marks `read_receipt_sent` in memory and history.

## Cloud Relay

`RelayClient` (singleton, one per platform) provides an HTTP fallback delivery
path using a Cloudflare Workers endpoint backed by KV storage.

**Why:** The existing LAN queue (in `config.json`) re-delivers messages only when
the sender's app is running and online. If Alice's machine is off when Bob
reconnects, Bob never receives Alice's queued messages. The cloud relay closes
this gap.

**How:**

1. Each device derives `relay_id = SHA256(private_key || "relay-v1")` at startup.
2. `relay_id_hash = SHA256(relay_id)` is published in every discovery packet so
   peers know where to address cloud-relay messages.
3. When a message send fails, `RelayClient.store()` POSTs the ciphertext to the
   Worker under the recipient's `relay_id_hash`.
4. The receiving client polls the Worker for new ciphertext on three triggers:
   once at startup, every 30 seconds while the app is running, and whenever the
   local network transitions from unavailable to available. `RelayClient
   .fetchPending()` is authenticated by presenting `relay_id` and letting the
   Worker verify `SHA256(relay_id) == relay_id_hash`.
5. Retrieved messages are decrypted in `MessagingService.handleRelayMessage()` and
   removed from the Worker via `RelayClient.delete()`.

**Privacy:** The Worker stores only ciphertext already encrypted to the
recipient's X25519 key. Cloudflare cannot read message content. Metadata
(ciphertext size, hashed mailbox address, timing) is visible to Cloudflare.

**Graceful degradation:** If `relayWorkerURL` is empty in config, or any HTTP
call times out (6-second connect / 10-second total), the relay path is silently
skipped and the app behaves identically to before this feature was added.

**Worker endpoint:** `https://lan-messenger-relay.davefelzy20.workers.dev`
**KV namespace:** `lan-messenger-relay` (TTL 72 h per message)

## File Transfer Service

`FileTransferService` sends and receives encrypted files while keeping UI work
off the hot path.

macOS:

- public API and callbacks are on the main actor;
- outgoing blocking I/O runs on a dedicated serial dispatch queue;
- incoming chunk decrypt/write runs on a serial queue to preserve chunk order;
- progress callbacks are throttled to roughly 12 Hz.

Windows:

- packet handling is on the UI thread through `DispatcherQueue`;
- each incoming transfer owns a channel consumed by one background task;
- finalization is queued through the same channel to run after all writes;
- outgoing I/O runs in background tasks;
- progress callbacks are throttled to roughly 12 Hz.

A failed outgoing file remains queued and is retried when the peer reconnects.

## AppModel

`AppModel` is the root state object on both platforms.

It owns:

- discovered peers keyed by public key;
- active and archived conversation lists;
- selected peer IP;
- message lists keyed by peer IP;
- typing states;
- active transfer banners;
- migration prompt state;
- update availability/progress;
- LAN availability.

It wires callbacks from services to UI state and persistence. It also owns
conversation actions:

- archive/unarchive;
- delete/hide conversation;
- delete contact;
- add/update contact;
- start a new conversation;
- mark conversation read;
- queue or send files;
- deliver pending messages/files after peer discovery.

Important rule: random discovered peers do not automatically become conversations.
Conversation rows are created from saved contacts or existing history.

## UI Architecture

### macOS UI

Important files:

- `App/LanMessengerApp.swift`: app entry, AppKit delegate, main window,
  menu-bar extra, migration sheet.
- `UI/AppModel.swift`: root observable state.
- `UI/Sidebar`: conversations, new-message picker, archive sheet.
- `UI/Chat`: header, message list, composer, file transfer banner, message
  bubbles, reply interactions.
- `UI/Settings`: identity, dock policy, login item, inbox, updates, version.
- `UI/Theme.swift`: shared colors, bubbles, timestamp helpers.

The app can hide from the Dock and live in the menu bar. Closing the last window
does not terminate the app.

### Windows UI

Important files:

- `App.xaml.cs`: startup exception capture and crash logging.
- `MainWindow.xaml(.cs)`: shell, sidebar/content layout, tray icon, dialogs,
  hide-to-tray lifecycle.
- `UI/AppModel.cs`: root observable state.
- `UI/Sidebar`: conversation list, contacts, archive, contact dialogs.
- `UI/Chat`: chat page, composer, message bubbles, file banner.
- `UI/Settings`: identity, inbox, update settings, tray preferences.
- `UI/Theme.cs`: shared brushes and formatting helpers.

The tray icon is always present. Closing can hide to tray based on config.

## Update Architecture

Both platforms check GitHub Releases using `update_repo` from config, defaulting
to `felzy613/lan-messenger`.

Release tags:

- macOS platform release: `macos-vX.Y.Z`
- Windows platform release: `windows-vX.Y.Z`
- combined release: `release-winX.Y.Z-macA.B.C`

Updaters prefer combined releases when they include the needed asset, but they
also search platform releases for update-channel artifacts and SHA256 sidecars.

### macOS Update Install

1. Query releases.
2. Pick macOS ZIP asset.
3. Fetch SHA256 sidecar when available.
4. Download to app-data staging.
5. Verify size and SHA256.
6. Extract `.app`.
7. Write helper shell script.
8. Spawn helper, terminate current app, replace bundle, clear quarantine,
   verify codesign best-effort, re-register Launch Services, relaunch.

### Windows Update Install

1. Query releases.
2. Pick Windows installer EXE.
3. Fetch SHA256 sidecar when available.
4. Download to app-data staging.
5. Verify size and SHA256.
6. Acquire install lock.
7. Kill other `LanMessenger` processes.
8. Launch Inno Setup installer with elevation and silent flags.
9. Exit current process so files can be replaced.

## Packaging Architecture

### macOS Packaging

Canonical script: `scripts/macos/package.sh`.

It:

- generates Xcode project from `src/macos/project.yml`;
- builds Release with xcodebuild;
- signs ad-hoc or with Developer ID;
- optionally notarizes;
- stages `LAN Messenger.app`;
- produces ZIP, DMG, and PKG;
- writes SHA256 sidecars;
- preserves diagnostics in `src/macos/build/package.log`.

Validation scripts:

- `scripts/macos/validate-bundle.sh`
- `scripts/macos/validate-dmg.sh`
- `scripts/macos/smoke-test.sh`

### Windows Packaging

Windows CI:

- restores and tests with MSBuild/MSTest;
- publishes self-contained x64 WinUI app;
- copies VC++ runtime DLLs app-local;
- downloads `vc_redist.x64.exe`;
- runs Inno Setup using `src/windows-native/LanMessenger.iss`;
- writes SHA256 sidecar;
- smoke-tests install and startup.

The Inno installer:

- installs to Program Files;
- optionally creates desktop icon;
- optionally writes HKCU Run startup entry;
- installs VC++ runtime when needed;
- adds firewall rules for UDP `54231` and TCP `54232`;
- launches/relaunches the app after install/update.

## CI Architecture

Workflows:

- `pr-checks.yml`: macOS and Windows unit tests, plus PR summary comment.
- `build-macos.yml`: full macOS build/test/package/validate/publish.
- `build-windows.yml`: full Windows build/test/package/smoke/publish.
- `release.yml`: creates combined release from latest platform releases.
- `integrity-check.yml`: weekly release/version audit.

Composite actions:

- `report-failure`: fingerprints CI failures and creates or updates GitHub issues.
- `validate-version`: validates semver and checks release existence.

## Diagnostics

Runtime logs:

- macOS networking: app data `LanMessenger/Logs/client.log`, mirrored to
  `os_log`.
- macOS updates: app data or Library logs `update.log`.
- Windows networking: `%APPDATA%\LanMessenger\Logs\client.log`.
- Windows updates: `%APPDATA%\LanMessenger\Logs\update.log`.
- Windows startup crashes: `%APPDATA%\LanMessenger\crash.log`.

CI diagnostics:

- macOS package log: `src/macos/build/package.log`.
- macOS smoke logs: `smoke.log`, Console log excerpts, crash reports.
- Windows build/test logs: `build-output-windows.txt`,
  `test-output-windows.txt`.
- Windows smoke logs: `smoke.log`, Event Viewer excerpts, crash dumps.

## Common Failure Modes

| Symptom | Likely Area | First Checks |
|---|---|---|
| Peers do not appear | UDP discovery/interface selection/firewall | `client.log`, adapter list, UDP 54231 allowed |
| Message stays at one check | decrypt failure, receipt send failure, TCP close race | sender/receiver `client.log`, `sent_receipt`, key mismatch |
| File transfer freezes UI | progress spam or main-thread file I/O | progress throttling, chunk queues |
| Received file missing | inbox permission, temp rename failure | `FileTransferStore`, inbox path, disk space |
| History disappears after key import | wrong local private key | migration choice, secure store, `history.enc` |
| Windows startup crash on clean machine | VC++ runtime/libsodium/PRI resources | app-local DLLs, Inno redist, `.pri` publish target |
| macOS app launches with generic icon | asset catalog or AppIcon.icns issue | `generate_icon.py`, `validate-bundle.sh` |
| Updater downloads but refuses install | SHA256/size mismatch or wrong asset | update log, release assets/sidecars |
| Combined release missing one platform | sibling build not done or platform release missing | `release.yml`, platform pre-releases |

## Extending The System

Before adding a protocol field:

1. Decide whether it is wire-level or local-only.
2. Add it as optional if older clients can ignore it.
3. Update `PROTOCOL.md`.
4. Update both `PacketTypes` implementations.
5. Add tests on both platforms or update test vectors if crypto/framing changes.

Before changing storage:

1. Make decoders tolerate missing old fields.
2. Add migration or fallback behavior.
3. Update config/history docs.
4. Test existing files when possible.

Before changing networking:

1. Preserve UDP raw JSON and TCP frame format.
2. Consider multi-interface behavior.
3. Preserve self-suppression by IP and public key.
4. Validate on macOS and Windows or document the remaining platform verification.

Before changing packaging/updating:

1. Keep CI scripts and in-app updater asset naming in sync.
2. Keep SHA256 sidecars attached to platform releases.
3. Run the relevant smoke test.
4. Update [RELEASE_AND_OPERATIONS.md](RELEASE_AND_OPERATIONS.md).
