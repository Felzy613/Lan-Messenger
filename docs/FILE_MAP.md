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

## Documentation

| Path | Purpose |
|---|---|
| `docs/ARCHITECTURE.md` | End-to-end architecture and data-flow guide. |
| `docs/DEVELOPMENT.md` | Local setup, build/test commands, validation, and change workflow. |
| `docs/RELEASE_AND_OPERATIONS.md` | CI, packaging, releases, updater behavior, diagnostics, and incident triage. |
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

## macOS Networking Layer

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/Core/Networking/NetworkInterfaceMonitor.swift` | Enumerates eligible IPv4 interfaces, computes broadcast addresses, and publishes changes. |
| `src/macos/LanMessenger/Core/Networking/DiscoveryService.swift` | UDP discovery sockets, per-interface multicast/broadcast/unicast beacons, replies, goodbye/probe, receive loop, and self-suppression. |
| `src/macos/LanMessenger/Core/Networking/NetworkCoordinator.swift` | Owns network lifecycle, discovery, TCP listener, inbound frame validation, peer sessions, and callbacks. |
| `src/macos/LanMessenger/Core/Networking/PeerSession.swift` | Persistent TCP peer connection with reconnect backoff and serial outgoing queue. |
| `src/macos/LanMessenger/Core/Networking/PresenceEvaluator.swift` | Pure LAN presence state machine (Online/Probing/Offline) from `last_seen`; the testable core driving online/offline status. |

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
| `src/macos/LanMessenger/Core/Services/NetLogger.swift` | Structured network logger to app-data log file and `os_log`. |
| `src/macos/LanMessenger/Core/Services/ScreenshotService.swift` | Primary-display capture with Screen Recording permission gate; writes PNG to a temp dir and returns the path for the existing file-transfer pipeline. |

## macOS UI

| Path | Purpose |
|---|---|
| `src/macos/LanMessenger/UI/AppModel.swift` | Root observable state, service wiring, peer/contact/history migration, conversations, pending queues, updates, and actions. |
| `src/macos/LanMessenger/UI/Theme.swift` | Shared color palette, bubble colors, accent, and formatting helpers. |
| `src/macos/LanMessenger/UI/AvatarView.swift` | Avatar view supporting initials and base64 contact photos. |
| `src/macos/LanMessenger/UI/Sidebar/SidebarView.swift` | Conversation list, toolbar buttons, empty state, new-message picker, archive sheet. |
| `src/macos/LanMessenger/UI/Sidebar/ConversationRowView.swift` | Sidebar row rendering: avatar, preview, timestamp, unread count, typing, online state. |
| `src/macos/LanMessenger/UI/Sidebar/ContactsView.swift` | Contacts sheet, search, add-from-LAN scanner, contact editor, contact photos, naming flow. |
| `src/macos/LanMessenger/UI/Chat/ChatView.swift` | Chat detail view, header, message list, reply banner, transfer banner, read marking. |
| `src/macos/LanMessenger/UI/Chat/ComposerView.swift` | NSTextView-backed composer, Return-to-send, Shift+Return newline, drag/drop, file picker, and screenshot capture button. |
| `src/macos/LanMessenger/UI/Chat/MessageBubbleView.swift` | Text/file bubble rendering, status icons, reply chips, copy/show context menus; delegates to `MediaBubbleView` for image and video attachments. |
| `src/macos/LanMessenger/UI/Chat/MediaBubbleView.swift` | Inline image and video bubbles with async thumbnail decode (NSImage / AVAssetImageGenerator), an in-memory `ThumbnailCache`, and a modal preview sheet hosting `NSImageView`/`VideoPlayer`. |
| `src/macos/LanMessenger/UI/Chat/MediaTypes.swift` | Extension-based image/video classification (`MediaKind`) and `FinderReveal` helper that opens Finder with the file selected off the main thread. |
| `src/macos/LanMessenger/UI/Chat/FileTransferBannerView.swift` | In-chat transfer progress banner. |
| `src/macos/LanMessenger/UI/Settings/SettingsView.swift` | Identity, dock/menu-bar behavior, login item, inbox, update source/check/install, about section. |

## macOS Tests

| Path | Purpose |
|---|---|
| `src/macos/LanMessengerTests/ConfigStoreTests.swift` | Config and filename sanitization tests. |
| `src/macos/LanMessengerTests/CryptoTests.swift` | Session/history crypto round trips and known vector tests. |
| `src/macos/LanMessengerTests/FrameCodecTests.swift` | Frame codec and known frame tests. |
| `src/macos/LanMessengerTests/HistoryStoreTests.swift` | History encryption, cap, wrong-key, and known history vector tests. |
| `src/macos/LanMessengerTests/MessageStatusTests.swift` | Monotonic status behavior tests. |
| `src/macos/LanMessengerTests/NetworkInterfaceMonitorTests.swift` | Adapter filtering, broadcast, lifecycle, and observer tests. |
| `src/macos/LanMessengerTests/PacketValidatorTests.swift` | Packet validation and sanitization tests, including discovery/goodbye types. |
| `src/macos/LanMessengerTests/PresenceEvaluatorTests.swift` | LAN presence state-machine transitions (online/probing/offline). |
| `src/macos/LanMessengerTests/known_good_exchange.json` | Cross-platform crypto/framing/history test vectors. |

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
| `src/windows-native/LanMessenger/MainWindow.xaml.cs` | Window shell behavior, dialog orchestration, chat/archive page reuse, migration dialog, tray lifecycle. |

## Windows Assets

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/Assets/icon.ico` | Windows application icon. |
| `src/windows-native/LanMessenger/Assets/icon_32.png` | 32px PNG icon asset. |
| `src/windows-native/LanMessenger/Assets/icon_64.png` | 64px PNG icon asset. |
| `src/windows-native/LanMessenger/Assets/icon_256.png` | 256px PNG icon asset. |

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

## Windows Networking Layer

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/Core/Networking/NetworkInterfaceMonitor.cs` | Enumerates eligible IPv4 adapters, computes broadcast addresses, and publishes changes. |
| `src/windows-native/LanMessenger/Core/Networking/DiscoveryService.cs` | UDP discovery sockets, per-interface multicast/broadcast/unicast beacons, replies, goodbye/probe, receive loop, and Windows UDP reset handling. |
| `src/windows-native/LanMessenger/Core/Networking/NetworkCoordinator.cs` | Network lifecycle, TCP listener, inbound validation, session management, and UI-dispatched callbacks. |
| `src/windows-native/LanMessenger/Core/Networking/PeerSession.cs` | Persistent TCP peer connection with reconnect backoff and concurrent send/receive loops. |
| `src/windows-native/LanMessenger/Core/Networking/PresenceEvaluator.cs` | Pure LAN presence state machine (Online/Probing/Offline) from `LastSeen`; mirror of the macOS evaluator. |

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
| `src/windows-native/LanMessenger/Core/Services/LanLogger.cs` | Structured log writer under `%APPDATA%\LanMessenger\Logs`. |
| `src/windows-native/LanMessenger/Core/Services/CryptoRuntimeDiagnostics.cs` | One-time diagnostics for libsodium and VC++ runtime DLL availability. |
| `src/windows-native/LanMessenger/Core/Services/ScreenshotService.cs` | Primary-display capture via GDI `CopyFromScreen`; writes PNG to `%TEMP%\LanMessenger-Screenshots` and returns the path for the existing file-transfer pipeline. |

## Windows UI

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger/UI/AppModel.cs` | Root observable state, service wiring, peers, conversations, pending queues, contacts, read receipts, updates, and actions. |
| `src/windows-native/LanMessenger/UI/Theme.cs` | Shared brushes, colors, and formatting helpers. |
| `src/windows-native/LanMessenger/UI/AvatarControl.xaml` | Avatar control XAML. |
| `src/windows-native/LanMessenger/UI/AvatarControl.xaml.cs` | Avatar image/initial rendering logic. |
| `src/windows-native/LanMessenger/UI/Sidebar/SidebarControl.xaml` | Sidebar list UI. |
| `src/windows-native/LanMessenger/UI/Sidebar/SidebarControl.xaml.cs` | Sidebar row collection updates, selection, settings/contacts/archive events. |
| `src/windows-native/LanMessenger/UI/Sidebar/ConversationRowControl.xaml` | Conversation row XAML. |
| `src/windows-native/LanMessenger/UI/Sidebar/ConversationRowControl.xaml.cs` | Conversation row binding and visual updates. |
| `src/windows-native/LanMessenger/UI/Sidebar/ContactsPage.xaml` | Contacts page XAML. |
| `src/windows-native/LanMessenger/UI/Sidebar/ContactsPage.xaml.cs` | Contact list view models, add/search/edit/delete events. |
| `src/windows-native/LanMessenger/UI/Sidebar/ArchivedPage.xaml` | Archived conversation page XAML. |
| `src/windows-native/LanMessenger/UI/Sidebar/ArchivedPage.xaml.cs` | Archived list binding and open/back events. |
| `src/windows-native/LanMessenger/UI/Sidebar/ContactEditorDialog.cs` | Contact editor, peer picker, naming dialog, and new-message dialog implementations. |
| `src/windows-native/LanMessenger/UI/Chat/ChatPage.xaml` | Chat page XAML. |
| `src/windows-native/LanMessenger/UI/Chat/ChatPage.xaml.cs` | Chat binding, selected peer handling, read receipts, messages, reply behavior, transfers. |
| `src/windows-native/LanMessenger/UI/Chat/ComposerControl.xaml` | Composer XAML with text entry, send, attachment, screenshot, and drop target UI. |
| `src/windows-native/LanMessenger/UI/Chat/ComposerControl.xaml.cs` | Composer key handling, typing callbacks, send callbacks, file drop/picker, and screenshot-request event. |
| `src/windows-native/LanMessenger/UI/Chat/MessageBubbleControl.xaml` | Message/file bubble XAML including the inline image tile, video poster tile, and document action row. |
| `src/windows-native/LanMessenger/UI/Chat/MessageBubbleControl.xaml.cs` | Bubble rendering, inline media branching, "Open" / "Show in folder" actions, status visuals, reply interactions, and modal preview launch. |
| `src/windows-native/LanMessenger/UI/Chat/MediaTypes.cs` | Extension-based image/video classification (`MediaKind`) and `FileReveal` helper that calls `explorer.exe /select` off the UI thread. |
| `src/windows-native/LanMessenger/UI/Chat/MediaPreviewDialog.xaml` | Modal media viewer XAML (image / `MediaPlayerElement`). |
| `src/windows-native/LanMessenger/UI/Chat/MediaPreviewDialog.xaml.cs` | Modal viewer code-behind: lazy media-source binding, transport-control teardown on close, and "Show in folder" primary-button handling. |
| `src/windows-native/LanMessenger/UI/Chat/FileTransferBannerControl.xaml` | File transfer banner XAML. |
| `src/windows-native/LanMessenger/UI/Chat/FileTransferBannerControl.xaml.cs` | File transfer banner code-behind. |
| `src/windows-native/LanMessenger/UI/Settings/SettingsPage.xaml` | Settings dialog XAML. |
| `src/windows-native/LanMessenger/UI/Settings/SettingsPage.xaml.cs` | Settings save logic, inbox picker, update check/install UI, tray preferences. |

## Windows Tests

| Path | Purpose |
|---|---|
| `src/windows-native/LanMessenger.Tests/ConfigStoreTests.cs` | Config and filename sanitization tests. |
| `src/windows-native/LanMessenger.Tests/CryptoTests.cs` | Session/history crypto round trips and known vector tests. |
| `src/windows-native/LanMessenger.Tests/FrameCodecTests.cs` | Frame codec and known frame tests. |
| `src/windows-native/LanMessenger.Tests/HistoryStoreTests.cs` | History encryption, cap, wrong-key, and known history vector tests. |
| `src/windows-native/LanMessenger.Tests/MessageStatusTests.cs` | Monotonic status behavior tests. |
| `src/windows-native/LanMessenger.Tests/NetworkInterfaceMonitorTests.cs` | Adapter filtering and broadcast tests. |
| `src/windows-native/LanMessenger.Tests/PacketValidatorTests.cs` | Packet validation and sanitization tests, including discovery/goodbye types. |
| `src/windows-native/LanMessenger.Tests/PresenceEvaluatorTests.cs` | LAN presence state-machine transitions (online/probing/offline). |
| `src/windows-native/LanMessenger.Tests/known_good_exchange.json` | Cross-platform crypto/framing/history test vectors. |

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
| Updates | platform `UpdateService` files and release workflows |
| CI failure reporting | `.github/actions/report-failure`, platform workflows |
