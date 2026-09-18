# File Map

This is the file-by-file inventory for the current LAN Messenger repo. It is
meant to answer "where does this live?" and "what owns this behavior?" without
requiring a fresh source-code pass.

## Root

| Path | Purpose |
|---|---|
| `README.md` | Human entry point with project overview, quick start, docs map, features, ports, storage paths, and versioning. |
| `CLAUDE.md` | Operating guide for agents/developers: current architecture, commands, invariants, gotchas, and validation checklist. |
| `PROTOCOL.md` | Authoritative wire protocol, crypto, history, config, validation, and compatibility spec. |
| `.gitignore` | Ignores OS clutter, IDE files, Swift build output, generated Xcode project, release artifacts, local config, and logs. |
| `Images/Logo.png` | Master logo used to generate macOS and Windows icons. |
| `spikes/` | Throwaway diagnostics that answer a single question about a platform API. Not part of either app and not referenced by `LanMessenger.sln`. See `spikes/README.md`. |

## Documentation

| Path | Purpose |
|---|---|
| `docs/ARCHITECTURE.md` | End-to-end architecture and data-flow guide. |
| `docs/DEVELOPMENT.md` | Local setup, build/test commands, validation, and change workflow. |
| `docs/RELEASE_AND_OPERATIONS.md` | CI, packaging, releases, updater behavior, diagnostics, and incident triage. |
| `docs/REMOTE_DESKTOP.md` | Remote-desktop status, handoff, and the plan for the remaining workstreams. |
| `docs/FILE_MAP.md` | This file inventory. |

## Repo-Local Memory

These files are documentation for future work sessions. They are not runtime
state for the app.

| Path | Purpose |
|---|---|
| `memory/MEMORY.md` | Index of repo-local memory notes. |
| `memory/project_native_rewrite.md` | Current project status and native rewrite context. |
| `memory/project_file_layout.md` | Current source, docs, scripts, and release layout. |
| `memory/project_protocol_gotchas.md` | Protocol compatibility gotchas that should be rechecked before wire changes. |
| `memory/project_swift_build_notes.md` | Swift/macOS build and compiler notes discovered during native work. |
| `memory/feedback_document_all_work.md` | Reminder that the user wants comprehensive memory/docs maintained after work. |
| `memory/windows-reliability-audit.md` | 2026-07-01 Windows audit: TCP retry, heartbeat-driven pending redelivery, presence fix, timer crash shields, atomic saves. |
| `memory/macos-reliability-fixes.md` | The macOS half of that audit, plus a local Keychain test hang that is a dev-machine quirk rather than a code bug. |
| `memory/relay-system-audit.md` | Relay bugs found and fixed in 2026-05 and 2026-07: delivery-mode tracking, offline-only gating, confirmed-store-before-badge, durable outbox retry. |
| `memory/remote-desktop.md` | Remote-desktop working notes: where the feature stands and the traps that are not obvious from the code. |

## Version Files

| Path | Purpose |
|---|---|
| `version/macos.json` | Canonical macOS release version used by CI and packaging. |
| `version/windows.json` | Canonical Windows release version used by CI and packaging. |
| `src/macos/VERSION` | Legacy marker; not the CI source of truth. |
| `src/windows-native/VERSION` | Legacy marker; not the CI source of truth. |

## GitHub Workflows

| Path | Purpose |
|---|---|
| `.github/workflows/pr-checks.yml` | Runs macOS and Windows tests on PRs and posts an aggregate PR comment. |
| `.github/workflows/build-macos.yml` | Full macOS pipeline: preflight, tests, icons, package, validate, smoke test, platform release. |
| `.github/workflows/build-windows.yml` | Full Windows pipeline: preflight, restore, tests, publish, VC++ runtime, Inno installer, smoke test, platform release. |
| `.github/workflows/release.yml` | Orchestrates combined release from latest platform releases. |
| `.github/workflows/integrity-check.yml` | Weekly/manual release and version health audit. |

## GitHub Actions

| Path | Purpose |
|---|---|
| `.github/actions/report-failure/action.yml` | Composite action that fingerprints CI failures and creates/comments GitHub issues. |
| `.github/actions/validate-version/action.yml` | Composite action that validates semver and checks platform release existence. |

## Scripts

| Path | Purpose |
|---|---|
| `scripts/hooks/pre-commit` | Auto-bumps platform versions based on staged platform source changes. |
| `scripts/macos/package.sh` | Canonical macOS package pipeline for app build, signing, optional notarization, DMG, ZIP, PKG, and SHA256 sidecars. |
| `scripts/macos/validate-bundle.sh` | Validates `.app` bundle structure, Info.plist keys, icon resources, and code signing. |
| `scripts/macos/validate-dmg.sh` | Mounts a DMG, validates layout and embedded app, then unmounts. |
| `scripts/macos/smoke-test.sh` | Installs `.dmg`, `.pkg`, or `.zip`, launches the app, verifies it stays alive, and collects diagnostics on failure. |
| `scripts/windows/smoke-test.ps1` | Silently installs Windows EXE, launches the app, verifies stability, and collects diagnostics. |
| `scripts/shared/close-ci-issues.sh` | Comments on and closes open CI failure issues after a successful platform release. |

## macOS Project Root

| Path | Purpose |
|---|---|
| `src/macos/Package.swift` | SwiftPM executable and test package definition; primary dev build/test entry point. |
| `src/macos/project.yml` | XcodeGen spec for producing `LanMessenger.xcodeproj` for app packaging. |
| `src/macos/scripts/build_app.sh` | Local wrapper around root macOS packaging script, defaults to faster no-PKG build. |
| `src/macos/scripts/build_dmg.sh` | Local wrapper around root macOS packaging script for DMG-focused builds. |
| `src/macos/scripts/generate_icon.py` | Generates AppIcon PNG slots and best-effort `AppIcon.icns` from `Images/Logo.png`. |

## macOS App Metadata And Assets

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/Info.plist` | macOS app metadata: bundle ID, version keys, local network usage text, Bonjour services, icon/display metadata. |
| `src/macos/LanMessenger/LanMessenger.entitlements` | macOS app entitlements used for signing. |
| `src/macos/LanMessenger/Assets.xcassets/Contents.json` | Asset catalog root metadata. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/Contents.json` | AppIcon slot metadata consumed by asset catalog. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_16x16.png` | macOS AppIcon 16px 1x. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_16x16@2x.png` | macOS AppIcon 16px 2x. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_32x32.png` | macOS AppIcon 32px 1x. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_32x32@2x.png` | macOS AppIcon 32px 2x. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_128x128.png` | macOS AppIcon 128px 1x. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_128x128@2x.png` | macOS AppIcon 128px 2x. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_256x256.png` | macOS AppIcon 256px 1x. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_256x256@2x.png` | macOS AppIcon 256px 2x. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_512x512.png` | macOS AppIcon 512px 1x. |
| `src/macos/LanMessenger/Assets.xcassets/AppIcon.appiconset/icon_512x512@2x.png` | macOS AppIcon 512px 2x. |

## macOS App Entry

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/App/LanMessengerApp.swift` | SwiftUI app entry, AppKit delegate, dock policy, window controller, main split view, menu-bar extra, and migration prompt. |

## macOS Protocol Layer

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/Core/Protocol/PacketTypes.swift` | Codable packet structs and `ValidatedPacket` enum for discovery, text, typing, receipts, and file transfer. |
| `src/macos/LanMessenger/Core/Protocol/PacketValidator.swift` | Validates packet types, self-suppression, nonce size, file size, discovery packets, and filename sanitization. |
| `src/macos/LanMessenger/Core/Protocol/FrameCodec.swift` | Encodes/decodes TCP length-prefixed JSON frames and enforces frame size limits. |

## macOS Crypto Layer

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/Core/Crypto/KeyManager.swift` | Loads, creates, saves, and imports the X25519 private key in Keychain. |
| `src/macos/LanMessenger/Core/Crypto/SessionCrypto.swift` | X25519/HKDF/AES-GCM message and file chunk encryption/decryption. |
| `src/macos/LanMessenger/Core/Crypto/HistoryCrypto.swift` | Local encrypted history key derivation, encryption, and decryption. |
| `src/macos/LanMessenger/Core/Crypto/RemoteSessionCrypto.swift` | Remote-desktop media handshake: Noise-KK-shaped triple DH over the pinned identity keys, transcript binding, role-selected directional keys, counter nonces, and key confirmation. Separate from `SessionCrypto` because media crypto is long-lived, directional and forward-secret where message crypto is one-shot. Carries hand-rolled canonical JSON — Foundation escapes `/`, which would diverge from the C# transcript by a byte. |

## macOS Networking Layer

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/Core/Networking/NetworkInterfaceMonitor.swift` | Enumerates eligible IPv4 interfaces, computes broadcast addresses, and publishes changes. |
| `src/macos/LanMessenger/Core/Networking/DiscoveryService.swift` | UDP discovery sockets, per-interface multicast/broadcast/unicast beacons, replies, goodbye/probe, receive loop, and self-suppression. |
| `src/macos/LanMessenger/Core/Networking/NetworkCoordinator.swift` | Owns network lifecycle, discovery, TCP listener, inbound frame validation, peer sessions, and callbacks. |
| `src/macos/LanMessenger/Core/Networking/PeerSession.swift` | Persistent TCP peer connection with reconnect backoff and serial outgoing queue. |
| `src/macos/LanMessenger/Core/Networking/PresenceEvaluator.swift` | Pure LAN presence state machine (Online/Probing/Offline) from `last_seen`; the testable core driving online/offline status. |

## macOS Media Layer

Remote-desktop transport and codec. Layered so that only `SocketMediaLink` is
untestable; everything above it runs against in-memory doubles. See
[REMOTE_DESKTOP.md](REMOTE_DESKTOP.md).

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/Core/Networking/Media/MediaFrame.swift` | `MediaChannel`, `MediaFlags`, `MediaFrameHeader`, `MediaProtocolError`. The header keeps **both** `rawFlags` and the masked `flags` view, because the AEAD associated data is the 22 header bytes exactly as received — a normalised re-encode differs by one byte from any peer that sets a reserved bit. |
| `src/macos/LanMessenger/Core/Networking/Media/MediaFrameCodec.swift` | Seal/open for media frames. `encodeFrame` is the only sanctioned constructor, because `length` is `18 + sealedPayloadCount` and lives inside the AAD. 4 MiB cap, deliberately independent of `FrameCodec`'s 50 MiB JSON cap. |
| `src/macos/LanMessenger/Core/Networking/Media/MediaWriteScheduler.swift` | 16 KiB fragmentation and channel priority (control → input → cursor → stats → video) so a large keyframe cannot park a mouse move. Drops the **queued** video frame, never the in-progress one, whose abandonment would desync the peer's reassembler permanently. |
| `src/macos/LanMessenger/Core/Networking/Media/MediaReassembler.swift` | Per-channel fragment reassembly with dual byte and fragment caps, plus `MediaSequenceGate`, which enforces strictly increasing sequence numbers. `lastAccepted` starts nil so sequence 0 is accepted. |
| `src/macos/LanMessenger/Core/Networking/Media/MediaLink.swift` | The `MediaLink` seam — three methods, the entire socket surface — and `SocketMediaLink`, which adopts a detached descriptor, clears the inherited read timeout, sets `TCP_NODELAY`, and caps `SO_SNDBUF` at 128 KiB. Sends through `poll(POLLOUT)` rather than `SO_SNDTIMEO`, which Darwin silently ignores under a zero window. |
| `src/macos/LanMessenger/Core/Networking/Media/MediaFrameReader.swift` | `MediaFrameReader`, the blocking frame read loop over a `MediaLink`, and `MediaFrameWriter`, its single-caller counterpart. Both live here because they are the two halves of one seam. |
| `src/macos/LanMessenger/Core/Networking/Media/MediaSession.swift` | Owns a live media session's three execution contexts — read, write, timers — which **never share**. The read loop never returns, so a timer scheduled onto it would never fire; a starved watchdog would leave a host's screen captured after a viewer crash. |
| `src/macos/LanMessenger/Core/Networking/Media/RemoteSessionRegistry.swift` | The ~10 s accept window, one in-flight session per peer, and single-shot `session_id` lookup, with an injected clock so the window is testable without sleeping. |
| `src/macos/LanMessenger/Core/Networking/Media/H264Bitstream.swift` | AVCC ↔ Annex-B conversion, the biggest cross-platform risk in the feature. Pure byte manipulation with no CoreMedia types, so it can be tested against a real Windows encoder artefact instead of its own output. |
| `src/macos/LanMessenger/Core/Networking/Media/H264Encoder.swift` | `VTCompressionSession` wrapper: High profile, no frame reordering, zero frame delay, BT.709 tags, low-latency rate control set as a creation-time *encoder specification* (not a property), parameter sets re-read on every keyframe, and `kVTPropertyNotSupportedErr` tolerated on every property. |
| `src/macos/LanMessenger/Core/Networking/Media/H264Decoder.swift` | The receive half: Annex-B → `CMSampleBuffer`. Splits access units on **slices**, rebuilds the `CMVideoFormatDescription` only when the in-band parameter sets change, strips them from the sample data (a duplicate is a decode error, not a no-op), attaches `DisplayImmediately` and an accurate `NotSync`, and emits nothing until an IDR has arrived. No `VTDecompressionSession` — `AVSampleBufferDisplayLayer` is the decoder. |
| `src/macos/LanMessenger/Core/Networking/Media/VideoPresenter.swift` | The `VideoPresenter` protocol and `SampleBufferVideoPresenter`, its `AVSampleBufferDisplayLayer` implementation. Exists almost entirely for `requiresFlushToResumeDecoding`, which is cleared from two directions — before every enqueue, and on `didBecomeActive`, because a silent stream never reaches the first. Vends a layer; knows nothing about windows. |
| `src/macos/LanMessenger/Core/Networking/Media/ScreenCaptureSource.swift` | Continuous `SCStream` capture for the host path — not the screenshot path, and different from it in nearly every decision. Restarts with backoff on `didStopWithError` and on screen-parameter changes, gates out idle/blank frames, resolves Retina points to even pixel dimensions, and keeps its control queue strictly apart from SCK's delivery queue. |
| `src/macos/LanMessenger/Core/Networking/Media/VideoSendPipeline.swift` | Host video path: owns the encoder, converts AVCC to Annex-B with parameter sets in-band at every IDR, and latches a viewer's keyframe request onto the next captured frame — VideoToolbox has no "send an IDR now" call, and a still screen may not produce a frame for some time. |
| `src/macos/LanMessenger/Core/Networking/Media/VideoReceivePipeline.swift` | Viewer video path: reassembled frame → decoder → sample buffer, handed on rather than presented, because `VideoPresenter` is main-actor work and this runs on the session read queue. `reset(reason:)` is the other half of a presenter flush. |
| `src/macos/LanMessenger/Core/Crypto/PeerKeyTrust.swift` | Classifies the identity key behind an invite as `pinned`, `changedAtKnownAddress` or `unknown` — the distinction PROTOCOL.md's consent rules require the prompt to draw. Contacts are keyed *by* public key, so a changed key looks like a new contact; the signal is positional, a saved contact having last lived at that address under a different key. |
| `src/macos/LanMessenger/Core/Networking/Media/RemoteDesktopPolicy.swift` | The consent gate as one pure function, plus `RemoteDesktopMode` (off/on, stored as a string so an unrecognised value fails closed). A stranger is ignored, a contact is declined or prompted; trust is settled before the mode is consulted, so turning the feature on never widens who may reach the host. |
| `src/macos/LanMessenger/Core/Networking/Media/RemoteGrant.swift` | The two-stage grant ladder (`none → viewing → control`, no path to control except from viewing, `end()` terminal) and `RemoteConsentRequest` — everything the prompt displays, including the fingerprint and the trust warning, built once from the invite so what a user agreed to is exactly what the gate decided to ask about. |
| `src/macos/LanMessenger/Core/Networking/Media/RemoteHostIndicator.swift` | What the host indicator says, and `IndicatorPlacement` — the pure top-centre placement it re-derives rather than remembers. Immovable by design: a viewer with the mouse could otherwise drag the host's own warning off-screen. |
| `src/macos/LanMessenger/Core/Networking/Media/RemoteSessionStop.swift` | `RemoteStopReason` — every way a session ends, which ones can still put a `remote_end` on the wire, and the audit sentence for each — plus `RemoteKillSwitch`, the reserved `⌃⌥⌘⎋` combination and the list WS7 must never forward. |
| `src/macos/LanMessenger/Core/Networking/Media/RemoteAuditEntry.swift` | The audit record, stored as a `__REMOTE__:` marker prefix on an ordinary history entry so the history format is unchanged. Decodes tolerantly: a record from a newer build yields nil rather than taking the conversation down. |

## macOS Persistence Layer

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/Core/Persistence/ConfigStore.swift` | App config schema, app-data paths, save/load, inbox/log/update directories, and legacy Python config migration. |
| `src/macos/LanMessenger/Core/Persistence/HistoryStore.swift` | Encrypted message history, per-peer cap, read-receipt flags, status updates, deletion, and IP migration. |
| `src/macos/LanMessenger/Core/Persistence/MessageStatus.swift` | Central monotonic message-status ranking. |
| `src/macos/LanMessenger/Core/Persistence/FileTransferStore.swift` | Incoming temp file state, outgoing queues, active transfer tracking, and final filename deduplication. |

## macOS Services

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/Core/Services/MessagingService.swift` | Text send/receive, typing, receipts, pending message retry, reply metadata, and status updates. |
| `src/macos/LanMessenger/Core/Services/FileTransferService.swift` | File send/receive, encrypted chunks, ordered background writes, queued retry, and progress callbacks. |
| `src/macos/LanMessenger/Core/Services/NotificationService.swift` | UserNotifications wrapper for message and file notifications. |
| `src/macos/LanMessenger/Core/Services/UpdateService.swift` | GitHub release checks, ZIP download, SHA256 verification, extraction, helper script install, and relaunch. |
| `src/macos/LanMessenger/Core/Services/LoginItemService.swift` | macOS 13+ launch-at-login management through `SMAppService.mainApp`. |
| `src/macos/LanMessenger/Core/Services/NetLogger.swift` | Structured network logger to app-data log file and `os_log`. Owns the `LogChannel` enum, from which the bug-report export bundle is derived. |
| `src/macos/LanMessenger/Core/Services/CrashReporter.swift` | Uncaught-exception and fatal-signal handlers; abnormal-termination marker. |
| `src/macos/LanMessenger/Core/Services/RelayClient.swift` | HTTP client for the cloud relay mailbox: store, fetch, and delete offline records. |
| `src/macos/LanMessenger/Core/Services/RemoteDesktopService.swift` | App-facing surface for remote desktop: inbound `media_attach` adoption and control-packet handling. Deliberately not main-thread affine — `attachInbound` runs synchronously on the inbound socket's own thread, because the descriptor must leave the JSON read loop before that loop reads again. |
| `src/macos/LanMessenger/Core/Services/DockPolicyGuard.swift` | Keeps Dock presence in sync with `hide_from_dock`. AppKit promotes an `.accessory` process back to `.regular` on its own and emits no notification for it, so the guard re-asserts the preference on activation/window-key notifications plus a 3 s tick. Single owner of the activation policy; reads/writes `NSApp` through injected closures so the correction logic is testable. |
| `src/macos/LanMessenger/Core/Services/RelayControl.swift` | Control envelope that carries an edit or delete through the relay mailbox when the peer is off-LAN: `__CTRL__:` marker, encode/decode, target validation, and fresh-record-id generation. |
| `src/macos/LanMessenger/Core/Services/AttachmentStore.swift` | Durable on-disk home for attachments the app generates itself (screen captures, pasted bitmaps): directory resolution honouring `screenshot_dir`, filename timestamps, and collision-free naming. Not the system temp directory — history stores absolute paths. |
| `src/macos/LanMessenger/Core/Services/AttachmentPasteboard.swift` | Reads attachments off `NSPasteboard` (⌘V) and off drag-and-drop item providers; writes pasted bitmaps to disk; builds the `NSItemProvider` used when dragging an attachment out of a bubble. `decide` holds the files-beat-bitmap, bitmap-only-without-text paste precedence. |
| `src/macos/LanMessenger/Core/Services/ScreenshotService.swift` | Screenshot capture: `captureInteractive()` launches `/usr/sbin/screencapture -i` for a native drag-to-select-region/click-a-window overlay (primary flow); `capturePrimaryDisplay`/`getShareableWindows`/`captureWindow` are ScreenCaptureKit-based alternatives retained but no longer wired into the composer. All paths write a PNG to a temp dir and return the path for the existing file-transfer pipeline. |

## macOS UI

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/UI/AppModel.swift` | Root observable state, service wiring, peer/contact/history migration, conversations, pending queues, updates, and actions. |
| `src/macos/LanMessenger/UI/Theme.swift` | Shared color palette, bubble colors, accent, and formatting helpers. |
| `src/macos/LanMessenger/UI/AvatarView.swift` | Avatar view supporting initials and base64 contact photos. |
| `src/macos/LanMessenger/UI/RemoteDesktop/RemoteConsentView.swift` | The consent prompt. Names the peer in the headline, always shows the fingerprint monospaced, shows no badge on the safe path, and makes Decline the default button. |
| `src/macos/LanMessenger/UI/RemoteDesktop/RemoteConsentPresenter.swift` | `NSPanel` + `NSHostingView` presentation (not `openWindow`, which cannot carry a session payload and needs a materialised window scene), with the countdown and the `timeout` auto-decline. The ticker runs in `.common` run-loop mode so it does not pause during menu tracking. |
| `src/macos/LanMessenger/UI/RemoteDesktop/RemoteHostIndicatorView.swift` | The always-on-top strip: amber for viewing, red for control, a slowly pulsing dot, elapsed time, and Stop Control / Stop Sharing. |
| `src/macos/LanMessenger/UI/RemoteDesktop/RemoteHostIndicatorPresenter.swift` | Borderless non-activating `NSPanel` at `.statusBar` level, on all spaces, stationary, full-screen auxiliary, not movable, repositioned on every screen change. Every flag is load-bearing — the comment says which failure each one prevents. |
| `src/macos/LanMessenger/UI/RemoteDesktop/RemoteSessionGuard.swift` | The exits that are not a race: the Carbon global hotkey, and the system events (sleep, user switch, `com.apple.screenIsLocked`, app quit, network loss) that mean nobody is present to press Stop. Disarms as it fires, so a closing lid stops the session once rather than twice. |
| `src/macos/LanMessenger/UI/RemoteDesktop/RemoteAuditRowView.swift` | An audit record in the thread: centred, small, grey, no bubble — a fact about the conversation rather than part of it. |
| `src/macos/LanMessenger/UI/TypingIndicatorView.swift` | `TypingDotsView` (three dots pulsing in a staggered wave, Reduce Motion aware) and `TypingBubbleView`, the incoming-style bubble that carries them at the end of the thread. Shared by the chat header, the thread, and the sidebar row. |
| `src/macos/LanMessenger/UI/Sidebar/SidebarView.swift` | Conversation list, toolbar buttons, empty state, new-message picker, archive sheet. |
| `src/macos/LanMessenger/UI/Sidebar/ConversationRowView.swift` | Sidebar row rendering: avatar, preview, timestamp, unread count, online state, and the accent capsule of typing dots that covers the preview while the peer types. |
| `src/macos/LanMessenger/UI/Sidebar/ContactsView.swift` | Contacts sheet, search, add-from-LAN scanner, contact editor, contact photos, naming flow. |
| `src/macos/LanMessenger/UI/Chat/ChatView.swift` | Chat detail view, header, message list, reply banner, transfer banner, read marking, the thread-wide file drop target (`onDrop` + dashed-border overlay) that sends dropped files as attachments, and the measured scroll geometry (`ContentBottomKey` + viewport height) behind the jump-to-latest button and the "only auto-scroll when already at the bottom" rule, and the typing indicator (dots under the peer's name, plus the `threadEndID` wrapper at the end of the message list that holds the typing bubble and doubles as the scroll-to-bottom anchor). |
| `src/macos/LanMessenger/UI/Chat/ComposerView.swift` | `PastingTextView` (NSTextView subclass) composer, Return-to-send, Shift+Return newline, ⌘V paste-to-attach, file picker, per-conversation draft restore, and screenshot capture button (`screencapture -i` interactive overlay). `WindowPickerView`/`ScreenshotPreviewView` remain defined here; the window-picker sheet is no longer shown. |
| `src/macos/LanMessenger/UI/Chat/MessageBubbleView.swift` | Text/file bubble rendering, status icons, reply chips, copy/show/delete context menus, drag-out of the attached file, and "This message was deleted" placeholder; delegates to `MediaBubbleView` for image and video attachments. |
| `src/macos/LanMessenger/UI/Chat/MediaBubbleView.swift` | Inline image and video bubbles with async thumbnail decode (NSImage / AVAssetImageGenerator), an in-memory `ThumbnailCache` keyed by path plus the file's modification date and size (so an overwritten file re-decodes), drag-out of the attached file, and a modal preview sheet hosting `NSImageView`/`VideoPlayer`. |
| `src/macos/LanMessenger/UI/Chat/MediaTypes.swift` | Extension-based image/video classification (`MediaKind`) and `FinderReveal` helper that opens Finder with the file selected off the main thread. |
| `src/macos/LanMessenger/UI/Chat/FileTransferBannerView.swift` | In-chat transfer progress banner. |
| `src/macos/LanMessenger/UI/Settings/SettingsView.swift` | Identity, dock/menu-bar behavior, login item, inbox, update source/check/install, about section. |

## macOS Tests

| Path | Purpose |
|---|---|
| `src/macos/LanMessengerTests/AttachmentPasteboardTests.swift` | Paste precedence (files vs bitmap vs text), pasted-bitmap flavour/extension choice, drag item-provider decoding, and `AttachmentStore` naming/placement. |
| `src/macos/LanMessengerTests/ConfigStoreTests.swift` | Config and filename sanitization tests. |
| `src/macos/LanMessengerTests/DockPolicyGuardTests.swift` | Guards the Dock-presence invariant: an AppKit promotion back to `.regular` must be corrected, and a policy that already matches must be left alone. |
| `src/macos/LanMessengerTests/CryptoTests.swift` | Session/history crypto round trips and known vector tests. |
| `src/macos/LanMessengerTests/DiscoveryServiceQueueTests.swift` | Guards the discovery threading invariant: the blocking receive loop must not starve the beacon timer or socket rebuild. |
| `src/macos/LanMessengerTests/FrameCodecTests.swift` | Frame codec and known frame tests. |
| `src/macos/LanMessengerTests/RelayControlTests.swift` | Relay control envelope round-trip and rejection rules, plus the inbound-delete security gate. |
| `src/macos/LanMessengerTests/MessageEditTests.swift` | Message-edit rules: the requireIncoming security gate, attachments/deleted messages being uneditable, history back-compat, and `edit_message` packet validation. |
| `src/macos/LanMessengerTests/HistoryStoreTests.swift` | History encryption, cap, wrong-key, and known history vector tests. |
| `src/macos/LanMessengerTests/MessageStatusTests.swift` | Monotonic status behavior tests. |
| `src/macos/LanMessengerTests/NetworkInterfaceMonitorTests.swift` | Adapter filtering, broadcast, lifecycle, and observer tests. |
| `src/macos/LanMessengerTests/PacketValidatorTests.swift` | Packet validation and sanitization tests, including discovery/goodbye types. |
| `src/macos/LanMessengerTests/PresenceEvaluatorTests.swift` | LAN presence state-machine transitions (online/probing/offline). |
| `src/macos/LanMessengerTests/RemoteSessionCryptoTests.swift` | Remote-desktop handshake: triple DH, transcript binding, role assignment, directional keys, counter nonces, key confirmation, and the shared handshake vector. Every test maps to a failure that is silent, catastrophic, or both. |
| `src/macos/LanMessengerTests/MediaFrameTests.swift` | Media framing, mux and demux: header/AAD byte fidelity, sealed-payload length, fragmentation and drop policy, reassembly caps, sequence enforcement, and the shared frame vector. |
| `src/macos/LanMessengerTests/RemoteDesktopQueueTests.swift` | Guards the media threading invariant: a session's timers must never share a context with its blocking read loop. Direct descendant of `DiscoveryServiceQueueTests`; counts seams reachable only from the timer path. |
| `src/macos/LanMessengerTests/H264BitstreamTests.swift` | AVCC ↔ Annex-B conversion, asserted against `windows_h264_sample.h264` — real Microsoft encoder output, not our own. |
| `src/macos/LanMessengerTests/H264EncoderTests.swift` | Drives a real `VTCompressionSession` and asserts on the bitstream it emits, not on a mock: parameter-set shape, reported NAL length size, keyframe flag agreeing with the actual IDR, forced keyframes, and Annex-B convertibility. Includes the skipped fixture generator (`LANMSG_EMIT_H264_FIXTURE`). |
| `src/macos/LanMessengerTests/H264DecoderTests.swift` | The Windows → macOS conformance direction, plus the presenter. Decodes `windows_h264_sample.h264` to **60 pictures at 1280×720** through a real `VTDecompressionSession` used as an oracle — nothing of ours is on its answering side. Also covers access-unit splitting, mid-GOP join, flush recovery and keyframe-request debouncing. |
| `src/macos/LanMessengerTests/ScreenCaptureSourceTests.swift` | Everything about capture that is assertable without a Screen Recording grant: geometry, the `SCStreamConfiguration` mapping, frame-status gating, the capture clock, restart backoff, and that `start()` refuses rather than prompting. That `SCStream` delivers a frame is **not** covered and cannot be. |
| `src/macos/LanMessengerTests/VideoPipelineEndToEndTests.swift` | The whole video path in one process: encoder → Annex-B → scheduler → framing → AES-GCM → paired link → unseal → sequence gate → reassembly → decoder → a real `VTDecompressionSession`. Two `MediaSession`s with independently derived directional keys, so a swapped role fails here rather than on a live call. |
| `src/macos/LanMessengerTests/ProtocolCapabilityTests.swift` | The optional discovery `caps` field, mostly from the compatibility direction: an older client that omits it, a newer one advertising unknown tokens, and a malformed field that must be tolerated rather than drop the peer. Asserts the wire key and token string literally, because a rename on one platform is invisible to the other. |
| `src/macos/LanMessengerTests/PeerKeyTrustTests.swift` | Key-trust classification, leaning on the direction that matters: a stranger's key must never read as pinned. |
| `src/macos/LanMessengerTests/RemoteDesktopPolicyTests.swift` | The consent rules, tested like protocol rules rather than interface behaviour — exhaustively, and from the direction of the mistake that would be worst to ship. Includes the invariant that no prompt is ever raised for an unpinned key. |
| `src/macos/LanMessengerTests/RemoteConsentTests.swift` | The grant ladder, the decline wire tokens, and what the prompt says. Includes `RemoteConsentRenderTests`, a skipped generator that renders the dialog to PNGs for visual review. |
| `src/macos/LanMessengerTests/RemoteHostIndicatorTests.swift` | What the indicator says, elapsed-time formatting including a clock that went backwards, and placement across the visible frame, a second display and a screen narrower than the strip. Includes the skipped render generator. |
| `src/macos/LanMessengerTests/RemoteSessionStopTests.swift` | The kill shortcut's identity and its separation from Force Quit, which stop reasons can still notify the peer, and that the guard disarms as it fires. |
| `src/macos/LanMessengerTests/RemoteAuditTests.swift` | Audit storage round-trips, tolerance of malformed and future records, the prose each event produces, and that the marker prefixes cannot collide. Includes a skipped render generator. |
| `src/macos/LanMessengerTests/TestMediaLinks.swift` | In-memory `MediaLink` doubles. Not tests — the seam that makes everything above the socket exercisable without binding a port. |
| `src/macos/LanMessengerTests/known_good_exchange.json` | Cross-platform crypto/framing/history test vectors. |
| `src/macos/LanMessengerTests/remote_handshake_vector.json` | Shared remote-desktop handshake vector. Must stay byte-identical to the Windows copy. |
| `src/macos/LanMessengerTests/media_frame_vector.json` | Shared media-frame vector. Must stay byte-identical to the Windows copy. |
| `src/macos/LanMessengerTests/windows_h264_sample.h264` | 60 frames / 126 NAL units of real Microsoft H264 Encoder MFT output (129,547 bytes). **Cannot be regenerated without the Dell** — do not delete. |

## Windows Project Root

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger.sln` | Visual Studio solution for app and tests. |
| `src/windows-native/LanMessenger.iss` | Inno Setup installer script. |
| `src/windows-native/LanMessenger/LanMessenger.csproj` | WinUI app project, dependencies, publish settings, version, asset copy, and PRI publish fix. |
| `src/windows-native/LanMessenger.Tests/LanMessenger.Tests.csproj` | MSTest project and test-vector copy settings. |
| `src/windows-native/LanMessenger/app.manifest` | Windows app manifest. |
| `src/windows-native/LanMessenger/App.xaml` | WinUI application resource root. |
| `src/windows-native/LanMessenger/App.xaml.cs` | WinUI app startup, binding/resource diagnostics, unhandled exception capture, crash log and message box. |
| `src/windows-native/LanMessenger/MainWindow.xaml` | Main shell layout, sidebar/content columns, toolbar buttons, and tray icon. |
| `src/windows-native/LanMessenger/MainWindow.xaml.cs` | Window shell behavior, dialog orchestration, chat/archive page reuse, migration dialog, tray lifecycle, the taskbar unread-count overlay icon (`ITaskbarList3.SetOverlayIcon`), and the tray icon's red-dot badge swap (`TrayIcon.IconSource`) — both driven by `AppModel.TotalUnreadCount`. |

## Windows Assets

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/Assets/icon.ico` | Windows application icon. |
| `src/windows-native/LanMessenger/Assets/icon_32.png` | 32px PNG icon asset. |
| `src/windows-native/LanMessenger/Assets/icon_64.png` | 64px PNG icon asset. |
| `src/windows-native/LanMessenger/Assets/icon_256.png` | 256px PNG icon asset. |
| `src/windows-native/LanMessenger/Assets/BadgeDot.ico` | Small red-dot overlay icon shown on the taskbar button (via `ITaskbarList3.SetOverlayIcon`) when there are unread messages in any active conversation. |
| `src/windows-native/LanMessenger/Assets/icon_unread.ico` | `icon.ico` with a red-dot badge pre-composited into the corner; swapped in for the system tray icon (`TrayIcon.IconSource`) when there are unread messages. |

## Windows Protocol Layer

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/Core/Protocol/PacketTypes.cs` | System.Text.Json packet classes and validated packet union. |
| `src/windows-native/LanMessenger/Core/Protocol/PacketValidator.cs` | TCP/UDP validation, nonce checks, file size checks, self suppression, and Windows filename sanitization. |
| `src/windows-native/LanMessenger/Core/Protocol/FrameCodec.cs` | Sync/async TCP frame encoder/decoder and max-size enforcement. |

## Windows Crypto Layer

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/Core/Crypto/KeyManager.cs` | Loads, creates, saves, and imports X25519 private key protected by DPAPI. |
| `src/windows-native/LanMessenger/Core/Crypto/SessionCrypto.cs` | NSec X25519/HKDF/AES-GCM message and file chunk encryption/decryption. |
| `src/windows-native/LanMessenger/Core/Crypto/HistoryCrypto.cs` | Local encrypted history key derivation, encryption, and decryption. |
| `src/windows-native/LanMessenger/Core/Crypto/RemoteSessionCrypto.cs` | Mirror of the macOS remote-desktop handshake. NSec for X25519 only; BCL `HKDF.DeriveKey` for derivation. Hand-rolled canonical JSON — `System.Text.Json` escapes `/` and non-ASCII, which would diverge from the Swift transcript. |

## Windows Networking Layer

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/Core/Networking/NetworkInterfaceMonitor.cs` | Enumerates eligible IPv4 adapters, computes broadcast addresses, and publishes changes. |
| `src/windows-native/LanMessenger/Core/Networking/DiscoveryService.cs` | UDP discovery sockets, per-interface multicast/broadcast/unicast beacons, replies, goodbye/probe, receive loop, and Windows UDP reset handling. |
| `src/windows-native/LanMessenger/Core/Networking/NetworkCoordinator.cs` | Network lifecycle, TCP listener, inbound validation, session management, and UI-dispatched callbacks. |
| `src/windows-native/LanMessenger/Core/Networking/PeerSession.cs` | Persistent TCP peer connection with reconnect backoff and concurrent send/receive loops. |
| `src/windows-native/LanMessenger/Core/Networking/PresenceEvaluator.cs` | Pure LAN presence state machine (Online/Probing/Offline) from `LastSeen`; mirror of the macOS evaluator. |

## Windows Media Layer

Byte-for-byte mirror of the macOS media layer; the shared vectors are what keep
them honest. See [REMOTE_DESKTOP.md](REMOTE_DESKTOP.md).

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/Core/Networking/Media/MediaFrame.cs` | `MediaChannel`, `MediaFlags`, `MediaFrameHeader` (keeps `RawFlags` beside the masked view for AAD fidelity), and the frame codec. |
| `src/windows-native/LanMessenger/Core/Networking/Media/MediaWriteScheduler.cs` | 16 KiB fragmentation, channel priority, and the queued-frame drop policy. |
| `src/windows-native/LanMessenger/Core/Networking/Media/MediaReassembler.cs` | Per-channel reassembly with dual caps, plus the sequence gate. |
| `src/windows-native/LanMessenger/Core/Networking/Media/MediaLink.cs` | The `IMediaLink` seam and the socket implementation. |
| `src/windows-native/LanMessenger/Core/Networking/Media/MediaFrameReader.cs` | `MediaFrameReader` and `MediaFrameWriter`; mirror of the Swift file. |
| `src/windows-native/LanMessenger/Core/Networking/Media/MediaSession.cs` | Read, write and timer contexts that never share. |
| `src/windows-native/LanMessenger/Core/Networking/Media/RemoteSessionRegistry.cs` | Accept window, one in-flight session per peer, injected clock. |
| `src/windows-native/LanMessenger/Core/Networking/Media/H264Bitstream.cs` | AVCC ↔ Annex-B conversion; mirror of the Swift implementation. |
| `src/windows-native/LanMessenger/Core/Crypto/PeerKeyTrust.cs` | Mirror of the Swift key-trust classifier. |
| `src/windows-native/LanMessenger/Core/Networking/Media/RemoteDesktopPolicy.cs` | Mirror of the Swift consent gate, including `RemoteDesktopMode`. `AppConfig` serializes the raw string rather than the enum: `System.Text.Json` throws on an unknown enum value, which would take the rest of the config with it. |

There is **no Windows encoder or decoder yet**. `spikes/windows-mf-probe` holds
working Media Foundation encode and decode code against this exact hardware, and
is the thing to port rather than starting from scratch.

## Windows Persistence Layer

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/Core/Persistence/ConfigStore.cs` | App config schema, app-data paths, save/load, and legacy Python config migration. |
| `src/windows-native/LanMessenger/Core/Persistence/HistoryStore.cs` | Encrypted history, cap, read flags, status updates, deletion, and IP migration. |
| `src/windows-native/LanMessenger/Core/Persistence/MessageStatus.cs` | Central monotonic message-status ranking. |
| `src/windows-native/LanMessenger/Core/Persistence/FileTransferStore.cs` | Incoming temp files, outgoing queues, active transfer state, and final filename dedupe. |

## Windows Services

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/Core/Services/MessagingService.cs` | Text send/receive, typing, receipts, pending retry, reply metadata, logs, and status updates. |
| `src/windows-native/LanMessenger/Core/Services/FileTransferService.cs` | File send/receive, encrypted chunks, per-transfer channels, queued retry, and progress callbacks. |
| `src/windows-native/LanMessenger/Core/Services/NotificationService.cs` | Windows toast notification wrapper. |
| `src/windows-native/LanMessenger/Core/Services/UpdateService.cs` | GitHub release checks, EXE download, SHA256 verification, elevated silent installer handoff, and exit. |
| `src/windows-native/LanMessenger/Core/Services/LanLogger.cs` | Structured log writer under `%APPDATA%\LanMessenger\Logs`. Owns the `LogChannel` enum, from which the bug-report export bundle is derived. |
| `src/windows-native/LanMessenger/Core/Services/RelayClient.cs` | HTTP client for the cloud relay mailbox: store, fetch, and delete offline records. |
| `src/windows-native/LanMessenger/Core/Services/RemoteDesktopService.cs` | App-facing surface for remote desktop: inbound `media_attach` adoption and control-packet handling. Mirror of the macOS service. |
| `src/windows-native/LanMessenger/Core/Services/CryptoRuntimeDiagnostics.cs` | One-time diagnostics for libsodium and VC++ runtime DLL availability. |
| `src/windows-native/LanMessenger/Core/Services/RelayControl.cs` | Control envelope that carries an edit or delete through the relay mailbox when the peer is off-LAN: `__CTRL__:` marker, encode/decode, target validation, and fresh-record-id generation. |
| `src/windows-native/LanMessenger/Core/Services/ClipboardAttachments.cs` | Decides what Ctrl+V in the composer means (files beat a bitmap; a bitmap only wins with no text alongside) and names pasted-image files. WinRT-free so the precedence rules compile and test off Windows. |
| `src/windows-native/LanMessenger/Core/Services/Win32FileDialog.cs` | Win32 open/save/folder dialogs (`GetOpenFileNameW`, `GetSaveFileNameW`, `SHBrowseForFolderW`) used instead of the WinRT pickers, whose shell broker throws `COMException 0x80004005` in this unpackaged process. STA/UI-thread only. |
| `src/windows-native/LanMessenger/Core/Services/PastedImageWriter.cs` | Decodes a clipboard bitmap and re-encodes it as PNG into the configured screenshot folder, returning the path for the existing file-transfer pipeline. |
| `src/windows-native/LanMessenger/Core/Services/ScreenshotService.cs` | Primary-display and per-window capture via GDI `CopyFromScreen`/`PrintWindow`; `CropToRegionAsync` crops a captured PNG to a pixel rectangle for the drag-to-select-region flow. Writes PNG to the configured screenshot folder (default `%USERPROFILE%\Downloads\LAN Messenger Screenshots`, never `%TEMP%` — history stores absolute paths) and returns the path for the existing file-transfer pipeline. |

## Windows UI

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/UI/AppModel.cs` | Root observable state, service wiring, peers, conversations, pending queues, contacts, read receipts, updates, and actions. |
| `src/windows-native/LanMessenger/UI/Theme.cs` | Shared brushes, colors, and formatting helpers. |
| `src/windows-native/LanMessenger/UI/AvatarControl.xaml` | Avatar control XAML. |
| `src/windows-native/LanMessenger/UI/AvatarControl.xaml.cs` | Avatar image/initial rendering logic. |
| `src/windows-native/LanMessenger/UI/TypingIndicatorControl.xaml` | Three dots plus the `Border` that becomes an incoming bubble for the thread copy. |
| `src/windows-native/LanMessenger/UI/TypingIndicatorControl.xaml.cs` | Staggered pulse storyboard built in code (targets the dot objects directly, so it works inside a `ListView` footer and a `DataTemplate`), `IsActive` start/stop, and the dot size/spacing/brush knobs the header and sidebar use. |
| `src/windows-native/LanMessenger/UI/Sidebar/SidebarControl.xaml` | Sidebar list UI. |
| `src/windows-native/LanMessenger/UI/Sidebar/SidebarControl.xaml.cs` | Sidebar row collection updates, selection, settings/contacts/archive events. |
| `src/windows-native/LanMessenger/UI/Sidebar/ConversationRowControl.xaml` | Conversation row XAML. |
| `src/windows-native/LanMessenger/UI/Sidebar/ConversationRowControl.xaml.cs` | Conversation row binding and visual updates, including swapping the message preview for the typing dots. |
| `src/windows-native/LanMessenger/UI/Sidebar/ContactsPage.xaml` | Contacts page XAML. |
| `src/windows-native/LanMessenger/UI/Sidebar/ContactsPage.xaml.cs` | Contact list view models, add/search/edit/delete events. |
| `src/windows-native/LanMessenger/UI/Sidebar/ArchivedPage.xaml` | Archived conversation page XAML. |
| `src/windows-native/LanMessenger/UI/Sidebar/ArchivedPage.xaml.cs` | Archived list binding and open/back events. |
| `src/windows-native/LanMessenger/UI/Sidebar/ContactEditorDialog.cs` | Contact editor, peer picker, naming dialog, and new-message dialog implementations. |
| `src/windows-native/LanMessenger/UI/Chat/ChatPage.xaml` | Chat page XAML. |
| `src/windows-native/LanMessenger/UI/Chat/ChatPage.xaml.cs` | Chat binding, selected peer handling, read receipts, messages, reply behavior, transfers, per-conversation draft save/restore (`AppModel.Drafts`), message deletion (`RequestDeleteMessage`), the thread-wide file drop target (`WireDropTargets` + `AcceptFileDrag`/`Page_Drop` + `DropOverlay`; handlers registered on `MessagesList`/`Composer` with `handledEventsToo` as well as on the page root, and strictly synchronous in `DragEnter`/`DragOver`), the jump-to-latest button, the typing indicator (`UpdateTypingIndicator` drives the header dots and the `ListView` footer bubble), and the screenshot capture flow including drag-to-select-region. |
| `src/windows-native/LanMessenger/UI/Chat/ComposerControl.xaml` | Composer XAML with text entry, send, attachment, and screenshot UI. File drops belong to `ChatPage`, which covers the whole thread; `InputBox` pins `AllowDrop="False"` so the TextBox can't claim one. |
| `src/windows-native/LanMessenger/UI/Chat/ComposerControl.xaml.cs` | Composer key handling, typing callbacks, send callbacks, file picker request, Ctrl+V paste-to-attach (`FilesPasted`), screenshot-request event, and a `Text` property used to save/restore per-conversation drafts. |
| `src/windows-native/LanMessenger/UI/Chat/MessageBubbleControl.xaml` | Message/file bubble XAML including the inline image tile, video poster tile, document action row, and the "Delete for Me" / "Delete for Everyone" context-menu items. |
| `src/windows-native/LanMessenger/UI/Chat/MessageBubbleControl.xaml.cs` | Bubble rendering, inline media branching, "Open" / "Show in folder" actions, status visuals, reply interactions, modal preview launch, drag-out of the attached file (`CanDrag` + `DragStarting`), the "deleted message" placeholder, and delete-menu click handlers. |
| `src/windows-native/LanMessenger/UI/Chat/ScreenshotDialogs.cs` | `ScreenshotWindowPickerDialog` (choose "Select region...", a window, or full screen) and `ScreenshotPreviewDialog` (send/cancel preview). |
| `src/windows-native/LanMessenger/UI/Chat/RegionSelectOverlayWindow.xaml.cs` | Full-screen borderless overlay over the primary display for drag-to-select-region screenshot capture; crops the pre-captured backing bitmap to the dragged rectangle. Code-only window — there is no matching `.xaml`. Primary-display only — multi-monitor and per-window hover highlighting are follow-ups. |
| `src/windows-native/LanMessenger/UI/Chat/MediaTypes.cs` | Extension-based image/video classification (`MediaKind`) and `FileReveal` helper that calls `explorer.exe /select` off the UI thread. |
| `src/windows-native/LanMessenger/UI/Chat/MediaPreviewDialog.xaml` | Modal media viewer XAML (image / `MediaPlayerElement`). |
| `src/windows-native/LanMessenger/UI/Chat/MediaPreviewDialog.xaml.cs` | Modal viewer code-behind: lazy media-source binding, transport-control teardown on close, and "Show in folder" primary-button handling. |
| `src/windows-native/LanMessenger/UI/Chat/FileTransferBannerControl.xaml` | File transfer banner XAML. |
| `src/windows-native/LanMessenger/UI/Chat/FileTransferBannerControl.xaml.cs` | File transfer banner code-behind. |
| `src/windows-native/LanMessenger/UI/Settings/SettingsPage.xaml` | Settings dialog XAML. |
| `src/windows-native/LanMessenger/UI/Settings/SettingsPage.xaml.cs` | Settings save logic, inbox/screenshot folder pickers and log-bundle export (all via `Win32FileDialog`), update check/install UI, tray preferences. |

## Windows Tests

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger.Tests/ClipboardAttachmentsTests.cs` | Ctrl+V paste precedence and pasted-image filename safety. |
| `src/windows-native/LanMessenger.Tests/ConfigStoreTests.cs` | Config and filename sanitization tests. |
| `src/windows-native/LanMessenger.Tests/CryptoTests.cs` | Session/history crypto round trips and known vector tests. |
| `src/windows-native/LanMessenger.Tests/FrameCodecTests.cs` | Frame codec and known frame tests. |
| `src/windows-native/LanMessenger.Tests/RelayControlTests.cs` | Relay control envelope round-trip, cross-platform decode, and rejection rules. |
| `src/windows-native/LanMessenger.Tests/MessageEditTests.cs` | Message-edit rules: the requireIncoming security gate, attachments/deleted messages being uneditable, history back-compat, and `edit_message` packet validation. |
| `src/windows-native/LanMessenger.Tests/HistoryStoreTests.cs` | History encryption, cap, wrong-key, and known history vector tests. |
| `src/windows-native/LanMessenger.Tests/MessageStatusTests.cs` | Monotonic status behavior tests. |
| `src/windows-native/LanMessenger.Tests/NetworkInterfaceMonitorTests.cs` | Adapter filtering and broadcast tests. |
| `src/windows-native/LanMessenger.Tests/PacketValidatorTests.cs` | Packet validation and sanitization tests, including discovery/goodbye types. |
| `src/windows-native/LanMessenger.Tests/PresenceEvaluatorTests.cs` | LAN presence state-machine transitions (online/probing/offline). |
| `src/windows-native/LanMessenger.Tests/RemoteSessionCryptoTests.cs` | Remote-desktop handshake, mirror of the macOS suite plus the shared vector. |
| `src/windows-native/LanMessenger.Tests/MediaFrameTests.cs` | Media framing, mux and demux, plus the shared frame vector. |
| `src/windows-native/LanMessenger.Tests/RemoteDesktopQueueTests.cs` | Media threading invariant; mirror of the macOS queue tests. |
| `src/windows-native/LanMessenger.Tests/H264BitstreamTests.cs` | AVCC ↔ Annex-B conversion against the real encoder artefact. |
| `src/windows-native/LanMessenger.Tests/ProtocolCapabilityTests.cs` | Mirror of the Swift `caps` suite; asserts the same wire key and token string. |
| `src/windows-native/LanMessenger.Tests/RemoteDesktopPolicyTests.cs` | Mirror of the Swift consent-gate suite, with the key-trust cases folded in. |
| `src/windows-native/LanMessenger.Tests/known_good_exchange.json` | Cross-platform crypto/framing/history test vectors. |
| `src/windows-native/LanMessenger.Tests/remote_handshake_vector.json` | Shared handshake vector. Must stay byte-identical to the macOS copy. |
| `src/windows-native/LanMessenger.Tests/media_frame_vector.json` | Shared media-frame vector. Must stay byte-identical to the macOS copy. |
| `src/windows-native/LanMessenger.Tests/windows_h264_sample.h264` | The same real-encoder artefact the macOS suite uses. |

## Spikes

Throwaway diagnostics. Not part of the shipping app, not referenced by
`LanMessenger.sln`, not built by CI. A spike is deleted once its question is
settled and the answer is written down in `docs/`.

| Path | Purpose |
|---|---|
| `spikes/README.md` | What each spike answers, where it must be run, and the results of every run so far. |
| `spikes/windows-mf-probe/Program.cs` | WS0 of the remote-desktop work: enumerates H.264 encoder MFTs, checks `ICodecAPI` reachability, tries Desktop Duplication, encodes NV12 to Annex-B, and (`--decode=`) feeds an Annex-B file to the Media Foundation decoder. The encode and decode stages are working reference code for the Windows half of WS4b and WS5. |
| `spikes/windows-mf-probe/WindowsMediaProbe.csproj` | Vortice-based project file for the probe. AnyCPU, so it reports the OS's own capability rather than the emulated one. |

## Generated Or Ignored Runtime Areas

These directories may exist locally but are not source:

| Path | Purpose |
|---|---|
| `dist/` | Generated release artifacts from packaging. |
| `builds/` | Historical/generated build artifacts; ignored. |
| `releases/` | Historical/generated release artifacts; ignored. |
| `Logs/` | Local runtime logs; ignored. |
| `.claude/worktrees/` | Claude/Codex worktrees; ignored. |
| `src/macos/.build/` | SwiftPM build output; ignored. |
| `src/macos/build/` | macOS packaging build directory; ignored. |
| `src/macos/LanMessenger.xcodeproj/` | Generated by XcodeGen; ignored. |

## Ownership Cheatsheet

| If you are changing... | Start here |
|---|---|
| Wire format | `PROTOCOL.md`, then both `Core/Protocol` trees |
| Encryption | both `Core/Crypto` trees and both crypto tests |
| Discovery | `DiscoveryService` and `NetworkInterfaceMonitor` on both platforms |
| Text message behavior | `MessagingService`, `HistoryStore`, `MessageStatus`, `AppModel` |
| File transfer behavior | `FileTransferService`, `FileTransferStore`, packet validation |
| Contacts/conversations | `AppModel`, platform sidebar/contact UI |
| macOS UI shell | `LanMessengerApp.swift`, `UI/*` Swift files |
| Windows UI shell | `MainWindow.xaml(.cs)`, `UI/*` XAML/C# files |
| macOS packaging | `scripts/macos/package.sh`, `src/macos/project.yml`, macOS workflow |
| Windows packaging | `LanMessenger.csproj`, `LanMessenger.iss`, Windows workflow |
| Remote desktop | `docs/REMOTE_DESKTOP.md` first, then `PROTOCOL.md` → Remote Desktop, then both `Core/Networking/Media` trees |
| Media framing or the media handshake | both `Core/Networking/Media` and `RemoteSessionCrypto` trees, **and both copies of `remote_handshake_vector.json` / `media_frame_vector.json`** |
| Updates | platform `UpdateService` files and release workflows |
| CI failure reporting | `.github/actions/report-failure`, platform workflows |
