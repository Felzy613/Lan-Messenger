# CLAUDE.md

This file is the working guide for agents and developers changing LAN Messenger.
It reflects the current native app tree, not the older Python/Tkinter codebase.

## Current Project Truth

LAN Messenger is now two native applications:

- macOS: Swift 5.9, SwiftUI, Swift Package Manager under `src/macos/`.
- Windows: C#/.NET 8, WinUI 3, Windows App SDK under `src/windows-native/`.

The apps must remain wire-compatible. Treat [PROTOCOL.md](PROTOCOL.md) as the
source of truth for networking, framing, crypto, validation, persistence formats,
and cross-platform behavior.

The repo does not commit `LanMessenger.xcodeproj`. macOS development uses
`Package.swift` for build/test and `project.yml` plus XcodeGen for app packaging.

## Documentation First Stops

- [README.md](README.md) - project overview and quick start.
- [PROTOCOL.md](PROTOCOL.md) - protocol and persistence spec.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) - system model, flows, services,
  storage, UI, updates, and failure modes.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) - local workflow and validation.
- [docs/RELEASE_AND_OPERATIONS.md](docs/RELEASE_AND_OPERATIONS.md) - CI,
  packaging, update channels, smoke tests, and diagnostics.
- [docs/FILE_MAP.md](docs/FILE_MAP.md) - detailed file inventory.
- [memory/](memory/) - repo-local project memory for future sessions.

Update the relevant docs when changing behavior, storage formats, protocol fields,
build commands, CI, packaging, or release behavior.

## Build And Test Commands

### macOS

```bash
cd src/macos
swift build
swift test
swift run
```

Generate an Xcode project only when needed:

```bash
cd src/macos
xcodegen generate
open LanMessenger.xcodeproj
```

Package locally through the same pipeline CI uses:

```bash
VERSION=$(jq -r '.version' version/macos.json) scripts/macos/package.sh
```

or from inside `src/macos`:

```bash
./scripts/build_app.sh
./scripts/build_pkg.sh
```

### Windows

Run on Windows with Visual Studio 2022, .NET 8, Windows App SDK support, and x64.
Use VS MSBuild for WinUI packaging tasks.

```powershell
cd src\windows-native
msbuild /t:Restore /p:Configuration=Release /p:Platform=x64 LanMessenger.sln
msbuild LanMessenger.Tests\LanMessenger.Tests.csproj /p:Configuration=Release /p:Platform=x64
$testDll = Get-ChildItem LanMessenger.Tests\bin -Filter LanMessenger.Tests.dll -Recurse | Select-Object -First 1
dotnet vstest $testDll.FullName --logger:"console;verbosity=normal"
```

Build the self-contained app:

```powershell
msbuild LanMessenger\LanMessenger.csproj `
  /t:Publish `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:RuntimeIdentifier=win-x64 `
  /p:SelfContained=true
```

## Repo Layout

```text
PROTOCOL.md
docs/
memory/
scripts/
version/
src/
  macos/
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
  windows-native/
    LanMessenger.sln
    LanMessenger/
      Core/
      UI/
    LanMessenger.Tests/
    LanMessenger.iss
```

## Architecture In One Page

Startup:

1. Platform app entry creates `AppModel`.
2. `AppModel` starts `NetworkCoordinator`.
3. `NetworkCoordinator` starts `NetworkInterfaceMonitor`, `DiscoveryService`,
   and the TCP listener.
4. `AppModel` wires `MessagingService`, `FileTransferService`,
   `NotificationService`, update checks, migration checks, and timers.

Discovery:

1. Every 1.5 seconds, discovery emits raw JSON over UDP 54231 to subnet broadcast,
   multicast `239.255.42.99`, limited broadcast, and extra unicast targets.
2. Receivers self-suppress by local IP and public key.
3. A `discovery` datagram gets a `discovery_reply` sent back to source IP on UDP
   54231.
4. `AppModel` upserts peers by public key and migrates saved contact history if
   the peer appears on a new IP.

Messaging:

1. The sender creates a 32-character lowercase hex `message_id`.
2. Plaintext is encrypted with X25519/HKDF/AES-GCM using `message_id` as AAD.
3. A one-shot TCP connection writes one framed JSON packet.
4. Receiver validates, decrypts, appends history, updates UI, and emits
   `sent_receipt`.
5. Opening a conversation sends `read_receipt` for unread incoming messages.
6. Status updates are rank-aware so late `Sent` callbacks cannot downgrade
   `Delivered` or `Read`.

File transfer:

1. Files use a separate TCP connection per transfer.
2. Sender writes `file_start`, many encrypted `file_chunk` packets, then
   `file_end`.
3. Each chunk uses `transfer_id` as AAD.
4. Receiver writes to `{transfer_id}_{filename}.part`, finalizes on `file_end`,
   and deduplicates final filenames.

Persistence:

- Config is JSON in the platform app-data directory.
- Private keys are not stored in config. macOS uses Keychain. Windows uses DPAPI.
- History is encrypted JSON, keyed by peer IP, capped at 200 messages per peer.
- Pending offline text and file queues live in config.

## Protocol Rules That Must Not Drift

Transport:

- UDP discovery port: `54231`.
- TCP message/file port: `54232`.
- UDP discovery is raw UTF-8 JSON with no frame prefix.
- TCP frames are 4-byte unsigned big-endian length plus UTF-8 JSON body.
- Reject frame sizes `<= 0` or `> 50 MiB`.
- Discovery replies go to `{source_ip}:54231`, not the TCP port.
- Discovery validators accept exactly three types: `discovery`,
  `discovery_reply`, and `goodbye`. A `goodbye` is a departure announcement —
  never reply to it, never let it refresh `last_seen`; it marks the peer offline.

Crypto:

- X25519 key agreement.
- HKDF-SHA256 with empty salt.
- Session info string: `lan-messenger`.
- History info string: `lan-messenger-history`.
- AES-256-GCM nonce is 12 bytes.
- Transmitted ciphertext is `ciphertext || 16-byte tag`, then base64.
- Text AAD is raw UTF-8 `message_id`.
- File chunk AAD is raw UTF-8 `transfer_id`.
- History AAD is raw UTF-8 `history-v1`.

IDs:

- `message_id` and `transfer_id` are `uuid4().hex` style values:
  32 lowercase hex characters, no dashes.

History:

- Keyed by peer IP address for compatibility.
- Capped to 200 entries per peer.
- Optional reply fields must decode cleanly when missing.

Files:

- Maximum advertised file size is 2 GiB.
- Chunk plaintext size is 64 KiB.
- Temp file format is `{transfer_id}_{filename}.part`.
- Dedup final names with `_1` through `_999`, then an 8-hex fallback.

Reply extension:

- Native clients may include `reply_to_message_id`, `reply_to_preview`, and
  `reply_to_sender` on `text` packets and history entries.
- These fields are optional and unencrypted metadata. Older clients ignore them.

Edit extension:

- Native clients may send `edit_message`, an encrypted replacement body for a
  message already sent. It is shaped like `text`, but `message_id` names the
  ORIGINAL message and the AAD is that same original id.
- History entries gain `edited` (bool, default false) and `edited_at` (optional
  float). `timestamp` keeps the original send time so an edit never moves a
  message in the thread.
- A receiver applies an edit only to a message that is *incoming from that
  peer*. The peer knows the `message_id` of everything we sent them, so an edit
  naming one of our own outgoing messages must be refused.
- Attachments (`__FILE__:` text) and deleted messages are never editable.
- Older clients reject `edit_message` as an unknown type and keep showing the
  original text.
- When the LAN write fails, edits and deletes fall back to a relay **control
  record** — an ordinary relay record whose plaintext is `__CTRL__:{json}`.
  It MUST use a fresh `message_id`, never the target's: the Worker dedups
  `/store` by `message_id` and answers a repeat with
  `{"ok":true,"duplicate":true}`, so a re-post under the original's id is
  discarded while reporting success. The Worker itself needs no changes and
  learns nothing.

## Platform-Specific Gotchas

### macOS

- Use `Darwin.bind(...)` inside classes that may collide with NSObject `bind`.
- `InputStream.read` with offsets must use `withUnsafeMutableBytes` and
  `advanced(by:)`.
- Delegate callbacks from background queues must hop to `Task { @MainActor ... }`
  or `DispatchQueue.main.async`.
- `ConfigStore.config` is mutable because services mutate nested value-type
  fields directly.
- POSIX filename sanitization splits on `/` only. Backslashes are not path
  separators on macOS and are intentionally preserved.
- SPM resources must live inside `src/macos`; test vectors are copied into
  `LanMessengerTests/`.
- `project.yml` is the XcodeGen source. Do not edit generated Xcode project files
  as durable source.
- `scripts/macos/package.sh` is the canonical packaging path.

### Windows

- Use Visual Studio MSBuild for WinUI projects. Plain `dotnet build` can miss
  Windows SDK/PRI packaging behavior.
- `LanMessenger.csproj` includes an `IncludePriFileInPublishOutput` target;
  removing it can break unpackaged WinUI startup with missing XAML resources.
- `NSec.Cryptography` depends on libsodium and the VC++ runtime. CI copies CRT
  DLLs app-local and the Inno installer also chain-installs `vc_redist.x64.exe`.
- `DiscoveryService` disables `SIO_UDP_CONNRESET` so ICMP port unreachable does
  not poison UDP sockets.
- Windows filename sanitization treats both `/` and `\` as path separators.
- Windows keeps peer records when offline so public keys remain available for
  offline queueing.

## Version Management

Canonical version files:

- `version/macos.json`
- `version/windows.json`

The pre-commit hook bumps platform versions when staged files are under the
corresponding platform tree:

```bash
cp scripts/hooks/pre-commit .git/hooks/pre-commit
chmod +x .git/hooks/pre-commit
```

Set `BUMP=minor` or `BUMP=major` for non-patch changes. The hook syncs:

- macOS: `version/macos.json` and `src/macos/project.yml`.
- Windows: `version/windows.json` and `src/windows-native/LanMessenger/LanMessenger.csproj`.

`src/macos/VERSION` and `src/windows-native/VERSION` are legacy markers and are
not CI sources of truth.

## CI And Release

- PR checks run macOS Swift tests and Windows MSTest.
- `Build macOS` runs tests, generates icons, builds/signs/packages PKG and ZIP,
  validates SHA256 sidecars, validates bundles/PKGs, smoke-tests install and
  launch, then publishes a `macos-vX.Y.Z` pre-release.
- `Build Windows` restores, tests, publishes self-contained WinUI output, bundles
  VC++ runtime DLLs, downloads the VC++ redistributable, builds an Inno installer,
  smoke-tests startup, then publishes a `windows-vX.Y.Z` pre-release.
- `Release Orchestration` combines latest platform releases into
  `release-winX.Y.Z-macA.B.C`.
- `Integrity Check` audits releases weekly.

## Validation Checklist Before Finishing Work

Use the smallest sufficient set for the change:

- Docs-only: `git diff --check` and grep for stale paths/claims.
- Protocol/crypto/framing: macOS `swift test`, Windows test suite when on Windows
  or CI, and check both `known_good_exchange.json` copies.
- macOS source: `cd src/macos && swift build && swift test`.
- Windows source: restore/build/tests through MSBuild on Windows.
- Packaging: platform workflow scripts or the relevant smoke test.
- UI behavior: launch the app on the target OS and exercise the changed flow.

## Do Not Accidentally Regress These Behaviors

- Do not show random discovered peers as conversations unless there is saved
  contact or message history.
- Do not delete contacts when hiding a conversation. Hidden conversations can be
  reopened from New Message.
- Do not downgrade message status from `Read` or `Delivered` to `Sent`.
- Do not store private keys in config JSON.
- Do not switch discovery to TCP or frame UDP packets.
- Do not rely on internet reachability for LAN availability.
- Do not regress presence to a single `now - last_seen` comparison, and do not
  delete peers the moment they go offline. Presence is an explicit per-peer
  state set by the evaluator, goodbye, and liveness probes; both platforms keep
  offline peers (their key is needed for queue/relay). See PROTOCOL.md → Presence
  and `PresenceEvaluator` on each platform.
- Do not put the blocking discovery receive loop on the same queue as the beacon
  timer or the interface-change socket rebuild. The loop never returns, so a
  shared serial queue starves both for the process lifetime: the host stops
  beaconing (peers can never discover it) and never rebuilds sockets after a
  DHCP change (every send fails `EADDRNOTAVAIL`). macOS uses a dedicated
  `recvQueue`; Windows uses a separate async `Task`. See
  `DiscoveryServiceQueueTests`.
- Do not reintroduce a verbose/debug logging toggle. All levels always write;
  a diagnostic level that is off by default is off exactly when a user hits the
  bug you needed it for. Volume is bounded by rotation.
- Do not remove the per-minute discovery health summary, or drop a log channel
  from the `LogChannel` enum — `archivedLogURLs`/`ArchivedLogPaths` derive the
  export bundle from that enum, so an unlisted channel silently never reaches a
  bug report.
- Do not remove SHA256 sidecar support from updaters; combined releases may only
  expose installer assets while sidecars live on per-platform releases.
- Do not make the only capture of SwiftUI's `openWindow` action live in
  `ContentView`. If the app relaunches with its window scene unmaterialised —
  a login-item start, or a restore of a session whose window was closed with the
  red X — `ContentView` never appears, the action stays nil, and the Dock icon
  becomes inert: every click runs `showMainWindow()`, finds no window to raise
  and no action to create one, and returns. The `MenuBarExtra` label always
  renders, so it must keep capturing it too.
- Do not treat the Dock activation policy as set-once. AppKit promotes an
  `.accessory` process back to `.regular` by itself (window-scene creation,
  modal panels, updater relaunch) and posts no notification for it, so a stray
  Dock icon reappears for a user who switched it off. `DockPolicyGuard` owns the
  policy and re-asserts it; route changes through it rather than calling
  `NSApp.setActivationPolicy` directly.
- Do not add a `DllImport` whose managed method name isn't the real exported
  entry point (or set `EntryPoint` explicitly). The failure is
  `EntryPointNotFoundException` at the first call, not at load, so it looks fine
  until the feature runs — `SetForegroundWindowInternal` in the screenshot
  region overlay crashed the app the moment the overlay opened. Nothing that
  runs inside a WinUI event handler may throw.
- Do not shrink the attachment drop target back to the composer strip, and keep
  every attachment route (picker, screenshot, drop, paste, drag-out) converging
  on `sendFile`/`SendFile` so queueing, offline persistence, and history stay
  identical. Paste precedence is files → bitmap-without-text → text; treating a
  bitmap that arrives alongside text as an attachment breaks ordinary text
  pasting from browsers, Word, and Outlook.
- Do not write generated attachments (screenshots, pasted images) to the system
  temp directory. History stores absolute paths, and temp is swept between
  reboots, so the bubble becomes "File no longer available" the next day.
- Do not assume re-posting to the relay under an existing `message_id` replaces
  anything. The Worker dedups and returns `{"ok":true,"duplicate":true}`, which
  every client treats as a successful store — so the stale copy stays and the
  peer receives the superseded text. Supersede a relay copy with a *new* record
  (a control record), never by re-uploading the old id.
- Do not let an inbound `edit_message` or `delete_message` rewrite or blank one
  of our own outgoing messages. The peer knows every `message_id` we ever sent them, so without the
  `requireIncoming` gate in `HistoryStore.applyEdit`/`ApplyEdit` a peer can
  silently rewrite what we said in our own transcript. Covered by
  `MessageEditTests`.
- Do not make release-notes generation fall back silently. The changelog
  boundary must be a ref git can resolve — not an unpublished draft's tag, and
  not the release currently being built — and a failure must warn and fall back
  rather than emit "no user-facing changes", which is indistinguishable from a
  genuinely empty release. That bug shipped empty notes for every pre-release.
- Do not treat generated Xcode project files as source.
