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
- [docs/REMOTE_DESKTOP.md](docs/REMOTE_DESKTOP.md) - remote-desktop status,
  handoff, and the plan for the remaining workstreams. Read this before touching
  anything under `Core/Networking/Media` or `RemoteSessionCrypto`.
- [memory/](memory/) - repo-local project memory for future sessions.

Update the relevant docs when changing behavior, storage formats, protocol fields,
build commands, CI, packaging, or release behavior.

## Build And Test Commands

### macOS

```bash
cd src/macos
swift build
swift test
swift run     # unbundled: no notifications, no TCC identity of its own
```

`swift run` produces a bare executable with no `.app` around it, and some
frameworks refuse to work there. `UNUserNotificationCenter.current()` is the one
that bites: it raises `NSInternalInconsistencyException` — "bundleProxyForCurrentProcess
is nil" — from inside a `dispatch_once` during `applicationWillFinishLaunching`,
so the app dies before its first window and the backtrace is sixty frames of
SwiftUI with the cause buried in the middle. `NotificationService` guards on
`Bundle.main.bundleIdentifier` for exactly this reason; anything else reaching
for a bundle-scoped API needs the same guard. TCC grants also attach to whatever
app is responsible for the process rather than to the binary, so permissions
behave differently here than in the packaged app.

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

Restore and build must be **separate MSBuild invocations**. Combined as
`/t:Restore,Build` the Windows App SDK targets are imported before the restore
that supplies them, so the XAML markup compiler never runs — and the build fails
with hundreds of `CS0103: The name 'InitializeComponent' does not exist` plus a
`CS5001: no static 'Main' method`, which reads as a broken source tree rather
than a restore ordering problem. Every generated partial goes missing at once;
that symptom means the markup compiler did not run.

```powershell
cd src\windows-native
msbuild /t:Restore /p:Configuration=Release /p:Platform=x64 LanMessenger.sln
msbuild LanMessenger.Tests\LanMessenger.Tests.csproj /p:Configuration=Release /p:Platform=x64
$testDll = Get-ChildItem LanMessenger.Tests\bin -Filter LanMessenger.Tests.dll -Recurse |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
"using $($testDll.FullName) built $($testDll.LastWriteTime)"
dotnet vstest $testDll.FullName --logger:"console;verbosity=normal"
```

`Sort-Object LastWriteTime` and the echo are not decoration. When a build fails,
the previous binary is still sitting there, and an unsorted `Select-Object -First 1`
runs it and reports a confident green from stale code — it reported 220/220 from a
three-day-old DLL once. Print the timestamp and check the totals moved.

When syncing this tree from a Mac, strip AppleDouble sidecars:

```bash
COPYFILE_DISABLE=1 tar czf out.tgz --exclude='bin' --exclude='obj' --exclude='._*' src/windows-native
```

macOS `tar` writes a `._Name` file for anything carrying an extended attribute.
Extracted on Windows those are real files, and the WinUI XAML compiler globs
`**/*.xaml`, so it parses `._SettingsPage.xaml` and dies with
`Xaml Internal Error error WMC9999: ... hexadecimal value 0x00 ... Line 1, position 1`.
One transfer scattered 130 of them. Note that `WMC9999` does not match an
`error CS|error MSB` log filter, so a filtered build log looks clean while the app
project has failed.

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
spikes/            throwaway diagnostics; not part of either app
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
          Media/   remote-desktop transport and codec
        Persistence/
        Services/
      UI/
    LanMessengerTests/
  windows-native/
    LanMessenger.sln
    LanMessenger/
      Core/
        Networking/
          Media/
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

Remote desktop (unreleased, `feat/remote-desktop-transport`):

- The media channel shares TCP `54232` by connection upgrade. A validated
  `media_attach` detaches the socket from the JSON read loop; the JSON frame
  format is untouched because the descriptor has left the loop before the first
  binary byte arrives. No new port.
- Media frame header is 22 bytes: `[4B length][1B channel][1B flags]
  [8B sequence][8B capture_us]`, and `length == 18 + sealedPayloadCount`.
- Media frames cap at 4 MiB, independent of the 50 MiB JSON cap.
- Media nonces are counters — `direction_salt(4) || sequence(8)` — never random.
- **The on-wire H.264 packaging is Annex-B with in-band SPS/PPS before every
  IDR.** Media Foundation speaks this natively; VideoToolbox does not, so the
  macOS side converts in both directions. See PROTOCOL.md → Video Sub-Channel.

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
- Remote desktop: both platform suites, plus **both** copies of
  `remote_handshake_vector.json` and `media_frame_vector.json`. Update
  `docs/REMOTE_DESKTOP.md` if the status of a workstream changed.
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
  bug report. Adding a channel without a writer is caught by the channel-coverage
  tests on both platforms, which enumerate the enum rather than a hardcoded list.
- Do not schedule the discovery health timer on `recvQueue`. It was created there
  for years and therefore never fired once — `recvQueue` is serial and the
  receive loop's block never returns, so the handler was never dequeued.
  Production logs showed 39,304 Discovery lines and zero health lines: the
  diagnostic built to catch queue starvation was itself dead from queue
  starvation. It must not go on `queue` either, since it has to survive a wedged
  beacon queue in order to report `tx_beacons=0`. It owns `healthQueue` for
  exactly that reason. Covered by `DiscoveryServiceQueueTests`.
- Do not give a media session's timers the same queue or thread as its read
  loop. `MediaSession` owns three contexts — read, write, timers — and the read
  loop never returns while running, so a keepalive or watchdog scheduled onto it
  is never dequeued. That is the same failure the discovery receive loop caused
  twice. A starved watchdog is the worst case here: a crashed viewer would leave
  the host's screen captured indefinitely with nothing saying so. Covered by
  `RemoteDesktopQueueTests` on both platforms, which count seams reachable only
  from the timer path.
- Do not rebuild a media frame header to use as AEAD associated data. The AAD is
  the 22 header bytes exactly as received; reserved flag bits 3-7 must be ignored
  for interpretation but preserved byte-for-byte, so a normalised re-encode
  differs by one byte against any peer that sets one and fails every tag check.
  Both `MediaFrameHeader` types keep `rawFlags` beside the masked view for this.
- Do not compute a media frame's `length` from the plaintext. It is
  `18 + sealedPayloadCount`, tag included, and the length lives inside the AAD —
  getting it wrong is off by exactly 16 and every frame fails on the peer with no
  other symptom. `encodeFrame`/`EncodeFrame` is the only sanctioned constructor.
- Do not advance a single byte after matching an H.264 start code. A 4-byte
  start code *contains* a 3-byte one at offset+1, so a scanner that does not
  consume the whole code finds a phantom unit inside every 4-byte code and
  reports exactly twice as many NAL units as exist. The tell is suspiciously
  equal 3-byte and 4-byte counts in the same stream. Both `H264Bitstream`
  scanners consume the full code; `H264BitstreamTests` asserts the count against
  a real encoder artefact.
- Do not split an H.264 stream into access units on access unit delimiters or
  parameter sets. VideoToolbox emits no AUDs at all and parameter sets only at
  IDRs, so an AUD/SPS split collapsed a 60-frame stream into 2 units and the
  Windows decoder emitted almost nothing. **A slice (NAL type 1 or 5) is the
  access unit boundary**; any SPS/PPS/SEI ahead of it belongs to it.
- Do not add a `remote_decline` reason on one platform only. The token set is
  fixed by PROTOCOL.md — `declined`, `busy`, `unsupported`, `disabled`,
  `no_encoder`, `timeout` — and the Windows enum carried only two of the six for
  a while, which meant that side had no way to say `declined` or `timeout`: the
  two a consent prompt actually produces. The tokens are spelled out in
  `ToToken`/`Parse` rather than derived from the enum names, because
  `no_encoder` is not `NoEncoder` lowercased and the two platforms have to agree
  byte for byte. Covered by `EveryDeclineReasonRoundTripsThroughItsWireToken`.
- Do not end a remote-desktop session by dropping the `MediaSession` reference.
  Forgetting it leaves the socket open, so the peer never learns: their
  indicator stays up, their capture keeps running, and the registry keeps the
  session id in flight so the next invite is refused as `busy` — which presents
  as "it worked once and now it will not start again". Call `stop()`/`Stop()`,
  which closes the socket (the one signal that always arrives, because a
  crashing peer sends nothing else), and send `remote_end` alongside it for the
  reason. Both platforms route every exit through one place — `announceEnd` on
  macOS, `AnnounceEnd` on Windows — because a session can end from six
  directions and doing it per call site is how five of them forget.
- Do not replace a `MediaSession`'s `OnClosed`/`onClosed` handler. `AttachInbound`
  already installs one that frees the registry entry, so overwriting it strands
  the peer as in-flight forever. Compose: call the previous handler, then your
  own. Covered by `AClosedSessionFreesThePeerForANewInvite`.
- Do not leave `hidesOnDeactivate` at its default on a remote-desktop viewer
  panel. `NSPanel` hides itself when its app deactivates, which is right for a
  utility palette and wrong for a window showing another machine's screen —
  clicking any other app makes the thing you are watching vanish, and somebody
  driving a remote machine is by definition not activating this app.
- Do not open a remote-desktop accept window after sending `remote_accept`. The
  peer may attach the instant it reads the answer, and a `media_attach` with no
  matching window is dropped and the connection closed — so the wrong order
  fails only when the network is fast enough to win the race, which is the worst
  kind of intermittent. `RemoteInviteCoordinator` opens the window first on both
  platforms, and both suites assert it by snapshotting the registry at the
  moment the accept frame is written.
- Do not start capturing when an invite is accepted. Capture begins when the
  viewer's `media_attach` actually arrives, so a peer that changes its mind
  never causes the host's screen to be read at all — the accept window simply
  expires. Agreeing is not sharing, and the gap between the two is a real
  network round trip.
- Do not let a malformed optional discovery field drop the datagram. A peer
  with a broken `caps` is still a peer, and rejecting its beacon removes it from
  the network entirely over a field that is optional by definition. Swift's
  decoder is tolerant by construction; `System.Text.Json` throws, which
  `ValidateDiscovery` catches as "drop it" — hence `TolerantStringListConverter`.
  The two must agree, because the peer that vanishes is always the one on the
  *other* platform. Covered by `ProtocolCapabilityTests` on both sides.
- Do not add a message-text marker prefix without updating every call site that
  inspects `text`. There are three — the sidebar's last-message preview, the
  editability guard in `AppModel`, and the chat row builder in `ChatView` — and
  one that forgets renders the raw JSON body at the user. `__FILE__:` marks an
  attachment and `__REMOTE__:` marks a remote-desktop audit record; both keep
  the stored history format unchanged, which is the whole reason for the trick.
  Covered by `RemoteAuditTests`.
- Do not route every inbound media frame to the video pipeline. Frames carry a
  channel — video, control, input, cursor, stats — and sending them all to the
  decoder worked only while nothing else was being sent: a control message handed
  to a decoder is not an error anywhere, it simply produces no picture. Both
  platforms dispatch on `frame.channel`/`frame.Channel` now, and input is gated
  twice over — a viewer has no injector at all, and the injector re-checks the
  grant at every call rather than trusting its caller.
- Do not normalize remote-desktop input against the viewer's window. The picture
  is aspect-fitted, so there are letterbox bars whenever the window's shape does
  not match the remote screen's, and a click in a bar is not a click on the
  remote machine. Normalize against the video rectangle, and return nothing for
  a point in the bars rather than clamping it to an edge — clamping puts the
  pointer where the user did not aim. Both capture paths compute the fitted
  rectangle the same way their presenter does.
- Do not write a second key table for the reverse direction. `HidKeyMap` is the
  single source of truth on each platform and the capture side inverts it:
  macOS's `RemoteHidUsage` and Windows's `HidKeyMap.UsageForScanCode` are both
  built from the forward table at startup, because a second hand-written table
  is a second place for the same typo. On Windows the extended flag is part of
  the inverse key — keypad 7 and Home share scan code 0x47, and inverting on the
  code alone makes the arrow keys type digits. Covered by the round-trip tests
  in both suites.
- Do not leave a remote-desktop session holding keys. A session that ends
  mid-chord leaves the host with whatever was down, and a machine with Alt or
  Command stuck behaves as if possessed — the user's first instinct is to blame
  their keyboard. Both injectors expose `releaseEverything`/`ReleaseEverything`
  and every teardown path calls it first, before capture stops. The viewer side
  lifts held keys too, when focus leaves and when capture stops: releasing a key
  outside the window means the key-up is never seen and so never sent.
- Do not register the remote-desktop kill shortcut through an `NSEvent` global
  monitor. That route needs the Accessibility grant, and the shortcut exists to
  work when things have gone wrong — including a grant that was never given or
  has been revoked. Carbon's `RegisterEventHotKey` needs no permission and fires
  whatever app is frontmost, which is the requirement: a host being actively
  controlled is by definition not looking at our window. The combination is
  `⌃⌥⌘⎋`, it lives in `RemoteKillSwitch`, and WS7's viewer-side capture must
  consult `RemoteKillSwitch.reserved` and never put it on the wire.
- Do not treat an absence of captured frames as a fault. `SCStream` and DXGI
  Desktop Duplication are both change-driven: a screen with nothing moving on it
  delivers no frames at all, indefinitely, and that is correct. A watchdog,
  viewer or reconnect timer that reads silence as failure tears down a perfectly
  healthy session the moment the user stops typing. The keepalive and stats
  sub-channels exist precisely so liveness is never inferred from video.
- Do not let a capture source's restart path share a queue with its frame
  delivery. `ScreenCaptureSource` keeps `frameQueue` for SCK and `controlQueue`
  for start/stop/restart, and blocks on stream teardown on the latter — the
  restart has to run while delivery is wedged, which is exactly when it is
  needed, and teardown must never block the queue SCK is delivering to. Same
  family as the `DiscoveryService` and `MediaSession` rules above.
- Do not drive an asynchronous MFT synchronously. `MF_TRANSFORM_ASYNC_UNLOCK`
  grants permission to drive a hardware encoder the async way; it does not make
  it behave like a synchronous one. An async MFT tells you when to act:
  `METransformNeedInput` means you may call `ProcessInput` exactly once,
  `METransformHaveOutput` means you may call `ProcessOutput` exactly once, and
  both arrive on the MFT's `IMFMediaEventGenerator`. Calling either at any other
  moment returns `E_UNEXPECTED` (0x8000FFFF) forever. On the test machine's
  `IntelAr Quick Sync Video H.264 Encoder MFT` that produced 34,731 identical
  error lines in fifty seconds and not one frame, while the viewer sat on
  "waiting for first frame". `EncoderKind` already distinguishes `HardwareAsync`
  from `HardwareSync`/`SoftwareSync`, and `H264Encoder` keeps both drive loops —
  the synchronous one is still what a machine with no Quick Sync uses.
- Do not log a repeating unrecoverable error and carry on. A fault that recurs
  every frame without recovering is not a log line; it is the end of the
  session. Left to spin it pegs a core, evicts everything else from the log, and
  presents to the user as a window that never does anything. `H264Encoder`
  counts consecutive failures, logs the first, and raises `OnFatalError` at
  `MaxConsecutiveFailures` so the session stops with a reason attached.
- Do not call Media Foundation's `ProcessOutput` before setting an output media
  type on the decoder. Without one it answers every call with
  `MF_E_TRANSFORM_TYPE_NOT_SET` (0xC00D6D60) and emits nothing, forever —
  including the `MF_E_TRANSFORM_STREAM_CHANGE` you were hoping to discover the
  real format from. Set a placeholder NV12 type up front and let the stream
  change correct it. In the same path, `MF_E_NOTACCEPTING` (0xC00D36B5) is
  normal flow control, not an error, and input samples without timestamps are
  buffered forever. All four are recorded in `spikes/README.md`.
- Do not compute an NV12 chroma offset from the height you are displaying. The
  planes are laid out with the *surface* height, and H.264 codes in 16-pixel
  macroblocks, so a 1080-line picture lives in a 1088-line surface and chroma
  begins after all 1088 rows. `MF_MT_FRAME_SIZE` reports the surface;
  `MF_MT_MINIMUM_DISPLAY_APERTURE` reports the rectangle worth showing, and
  cropping to it without carrying the surface height along moves the chroma read
  eight rows out of place. The picture stays perfectly sharp — luma is untouched
  — and every colour in it is wrong, which reads as a broken decoder rather than
  as arithmetic in the presenter. `DecodedVideoFrame` carries `SurfaceHeight`
  beside `Height` for this, and `Nv12Converter.ToBgra` takes both. Covered by
  `ChromaIsReadFromTheSurfaceHeightNotTheDisplayHeight`.
- Do not pass `MFCreateMemoryBuffer` straight into `AddBuffer`. The create
  returns a reference **the caller owns**, and `AddBuffer` takes its own rather
  than adopting yours, so `sample.AddBuffer(MFCreateMemoryBuffer(size))` leaks
  one native buffer every time it runs. Per frame at 1080p that was 208MB a
  second and 15GB in ninety seconds, in the decoder and the encoder alike. It is
  invisible from the C#: the code reads correctly, the managed heap stays flat,
  and only the process total moves — which is why `viewer_stats` reports `gc_mb`
  beside `ws_mb`. A flat managed heap under a climbing process means the leak is
  native and reading the C# will not find it.
- Do not allocate a frame-sized buffer per frame. At 1080p that is a few
  megabytes, which lands on the Large Object Heap — not compacted, and swept
  only on a gen2 collection. The capture converter and the decoder were each
  allocating one per frame, about 80MB a second between them; ten minutes of
  self-view took the process to **32GB of private commit** and the machine to
  668MB free. The visible symptom was not the app at all: `csc.exe` began
  exiting with code -1 during the WinUI build, and MSBuild reported the inline
  task it had failed to compile as `MSB4036: task not found`, which names
  neither memory nor LAN Messenger. Both paths now grow one buffer on demand and
  reuse it, which is safe because `CapturedFrame.Nv12` is consumed inside the
  capture-loop iteration that produced it and `DecodedVideoFrame` is documented
  as valid only until the next decode.
- Do not pace a capture loop with `Thread.Sleep`. It rounds up to the system
  timer granularity, 15.6ms by default, so a requested 21ms becomes about 31ms
  and a loop with 12ms of work in it settles at 21fps while believing it is
  asking for 30. `AcquireNextFrame` is already the correct wait: it blocks until
  the desktop actually changes, costs nothing while waiting, and does not round.
  Duplication has to be drained regardless — an unreleased frame blocks the next
  acquire — so pace by declining the colour conversion, not by declining to
  acquire. Frames then arrive as fresh as the compositor can make them, which
  the `age_ms` figure in `capture_stats` reports.
- Do not hard-code an H.264 NAL length prefix size. Read it from the format
  description. VideoToolbox emits 4 in practice, which is exactly why
  hard-coding it survives testing and fails later against another encoder. The
  sole exception is `H264Decoder.nalLengthSize`, where the same value is written
  into the format description and the length prefixes we author ourselves.
- Do not put H.264 parameter sets in the `CMVideoFormatDescription` *and* the
  sample data. VideoToolbox treats the duplicate as a decode error rather than
  as redundancy, and the error surfaces as a picture that never appears.
  `H264Bitstream.annexBToAVCC` drops SPS/PPS/AUD by default for this reason;
  passing a narrower `dropping:` set re-introduces the bug.
- Do not enqueue to an `AVSampleBufferDisplayLayer` without clearing
  `requiresFlushToResumeDecoding`, and do not clear it from only one place. The
  layer silently discards everything enqueued while the flag is set, and it is
  set by occlusion and focus loss — this is the "video froze after I switched
  apps" bug. `SampleBufferVideoPresenter` checks it before every enqueue *and*
  hooks `NSApplication.didBecomeActiveNotification`, because screen capture is
  change-driven on both platforms and a still screen sends no frames for the
  enqueue check to run on. Every flush must be followed by a keyframe request.
- Do not delete `windows_h264_sample.h264` from either test directory. It is
  real Microsoft H264 Encoder MFT output and cannot be regenerated without the
  Windows machine; it is the only thing that tests the Annex-B converter against
  something other than our own output.
- Do not drop the in-progress video frame when the two-frame budget is full. Once
  its first fragment is on the wire the peer is reassembling it, and abandoning it
  leaves a dangling `fragmented`-without-`final` that desyncs their reassembler
  permanently. Drop the queued frame, which is also the staler one.
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
- Do not call a WinRT picker (`FileOpenPicker`, `FileSavePicker`,
  `FolderPicker`) from the Windows app. They route through a shell-broker COM
  surrogate that throws `COMException 0x80004005` in this unpackaged process,
  and every call site is an `async void` click handler, so the throw is
  unhandled and kills the app. Export Logs did exactly that — the crash landed
  precisely when a user was trying to collect a bug report. Use
  `Win32FileDialog` (`GetOpenFileNameW` / `GetSaveFileNameW` /
  `SHBrowseForFolderW`), from the UI thread, never inside `Task.Run`.
- Do not cache a decoded image by file path alone. A bubble stores only an
  absolute path, so when a file is overwritten in place — re-exporting an image
  under the same name and sending it again is the ordinary case — a path-keyed
  cache keeps showing the version decoded first while the peer receives the new
  bytes, and the sent bubble silently disagrees with what was transmitted. On
  Windows that cache is XAML's own, keyed by URI for the life of the process:
  every `BitmapImage` for a mutable path needs
  `CreateOptions = BitmapCreateOptions.IgnoreImageCache` assigned *before*
  `UriSource` (setting `UriSource` starts the decode, and the options are read
  at that moment); fixed app assets such as the tray `.ico` may keep the cache.
  On macOS the cache is ours — `ThumbnailCache` keys on path plus the file's
  modification date and size, and must keep doing so.
- Do not add a `DllImport` whose managed method name isn't the real exported
  entry point (or set `EntryPoint` explicitly). The failure is
  `EntryPointNotFoundException` at the first call, not at load, so it looks fine
  until the feature runs — `SetForegroundWindowInternal` in the screenshot
  region overlay crashed the app the moment the overlay opened. Nothing that
  runs inside a WinUI event handler may throw.
- Do not `await` inside a WinUI `DragEnter`/`DragOver` handler, and do not let
  one throw. Awaiting returns control to the drag source, which reads
  `AcceptedOperation` at that instant and takes the unset value as a refusal —
  the drop is never offered and later drags break too
  (microsoft-ui-xaml#8108). Inspect the payload in `Drop`, under a deferral.
  `DragUIOverride` is null for some sources and throws a bare `COMException` on
  others; an unhandled throw out of a drag handler ends the process. Registering
  drop handlers only on the page root is also not enough — the `ListView`
  covering the thread has its own class handling for these events, so
  `ChatPage.WireDropTargets` re-registers them on the children with
  `handledEventsToo`.
- Do not raise a SwiftUI preference from inside a `.background()` or
  `.overlay()` subtree and expect `onPreferenceChange` to see it. On macOS 13/14
  the value stays at its default forever, silently. `ChatView`'s scroll geometry
  depends on this: the content-bottom sentinel is a real sibling inside the
  `ScrollView`, and the viewport height is written from `onAppear`/`onChange`
  rather than through a second preference key.
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
