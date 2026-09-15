# Development Guide

This guide covers local setup, common commands, validation, versioning, and safe
change workflow for LAN Messenger.

## Prerequisites

### macOS

- macOS 13 or newer for the app runtime.
- Xcode command line tools.
- Swift 5.9-compatible toolchain.
- `jq` for packaging/version helpers.
- `xcodegen` only when generating `LanMessenger.xcodeproj`.
- Python 3 and Pillow only when regenerating icons manually.

Install optional tools:

```bash
brew install xcodegen jq
python3 -m pip install Pillow
```

### Windows

- Windows 10 build 19041 or newer.
- Visual Studio 2022 with Windows App SDK/WinUI workloads.
- .NET 8 SDK.
- Inno Setup 6 for installer packaging.
- x64 build environment.

## Daily Workflow

1. Read [../PROTOCOL.md](../PROTOCOL.md) before protocol, crypto, networking,
   receipt, file-transfer, or history changes.
2. Make the smallest coherent change on each affected platform.
3. Keep cross-platform behavior symmetrical unless the platform difference is
   explicit and documented.
4. Run the relevant tests.
5. Update docs when commands, behavior, storage, packet fields, CI, or release
   behavior changes.
6. Check `git diff --check`.

## macOS Development

From repo root:

```bash
cd src/macos
swift build
swift test
swift run
```

Generate and open the Xcode project:

```bash
cd src/macos
xcodegen generate
open LanMessenger.xcodeproj
```

The generated Xcode project is ignored by git. Durable project settings live in
`src/macos/project.yml`.

### macOS Packaging

Fast local package from inside `src/macos`:

```bash
./scripts/build_app.sh
```

Canonical packaging from repo root:

```bash
VERSION=$(jq -r '.version' version/macos.json) scripts/macos/package.sh
```

Useful environment variables:

| Variable | Purpose |
|---|---|
| `VERSION` | Required by canonical script; wrapper reads from `version/macos.json` |
| `SIGNING_IDENTITY` | Developer ID Application identity; empty means ad-hoc signing |
| `NOTARIZE=1` | Enables notarization when notary credentials exist |
| `SKIP_PKG=1` | Skips PKG for faster local builds |
| `KEEP_BUILD=1` | Keeps `src/macos/build/` for debugging |
| `OUTPUT_DIR` | Overrides artifact destination |

Validate local artifacts:

```bash
scripts/macos/validate-bundle.sh "/path/to/LAN Messenger.app"
scripts/macos/validate-dmg.sh dist/macos/LanMessenger-macOS-<version>.dmg
scripts/macos/smoke-test.sh dist/macos/LanMessenger-macOS-<version>.dmg
```

## Windows Development

Use a Windows machine or CI runner. VS MSBuild is preferred for WinUI projects.

```powershell
cd src\windows-native
msbuild /t:Restore /p:Configuration=Release /p:Platform=x64 LanMessenger.sln
msbuild LanMessenger.Tests\LanMessenger.Tests.csproj /p:Configuration=Release /p:Platform=x64
$testDll = Get-ChildItem LanMessenger.Tests\bin -Filter LanMessenger.Tests.dll -Recurse | Select-Object -First 1
dotnet vstest $testDll.FullName --logger:"console;verbosity=normal"
```

Publish the self-contained app:

```powershell
msbuild LanMessenger\LanMessenger.csproj `
  /t:Publish `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:RuntimeIdentifier=win-x64 `
  /p:SelfContained=true
```

Build the installer after publishing:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=<version> LanMessenger.iss
```

Smoke-test an installer:

```powershell
scripts\windows\smoke-test.ps1 -ArtifactPath .\Output\LanMessenger-Setup-<version>.exe
```

## Remote Desktop Development

The feature and its remaining plan are documented in
[REMOTE_DESKTOP.md](REMOTE_DESKTOP.md). This section is only the mechanics.

It lives on `feat/remote-desktop-transport` and is **not** on `main`.

### What can be done from a Mac

Everything except Windows capture, Windows encode/decode, `SendInput`, and
running the WinUI app. The media transport, the handshake crypto and the
bitstream conversion are all pure logic with in-memory seams, so:

```bash
cd src/macos
swift test --filter RemoteSessionCryptoTests
swift test --filter MediaFrameTests
swift test --filter H264BitstreamTests
swift test --filter H264EncoderTests     # drives a real VTCompressionSession
swift test --filter H264DecoderTests     # drives a real VTDecompressionSession
swift test --filter VideoPipelineEndToEndTests   # capture-to-picture, one process
```

The Windows sources can be compiled and their tests run from a Mac through the
shim-csproj recipe in `memory/` — see the repo memory index. That covers
`Core/` and the tests; it cannot build the WinUI app itself.

### TCC grants

macOS signing is stable (`DEVELOPMENT_TEAM` is set in `project.yml`, bundle id
`com.dave.lanmessenger`), so Screen Recording and Accessibility grants survive a
rebuild. To reset one while testing:

```bash
tccutil reset ScreenCapture com.dave.lanmessenger
tccutil reset Accessibility com.dave.lanmessenger
```

### The Windows probe

`spikes/windows-mf-probe` answers the platform questions that cannot be answered
from a Mac, and its encode and decode stages are working reference code.

```powershell
cd spikes\windows-mf-probe
dotnet run -c Release
dotnet run -c Release -- --decode=macos_sample.h264
```

It always exits 0 — it is a report, not a gate. Read the output; "all stages
ran" is not "all stages are healthy". `spikes/README.md` records the results of
every run so far.

**Desktop Duplication and `SendInput` need an interactive console session.** An
SSH logon has no attached desktop, so those stages must be run at the physical
keyboard. Do not substitute Microsoft RDP: it creates a virtual session with its
own display driver, so Desktop Duplication would describe the RDP display rather
than the real one, and some GPU drivers disable their hardware encoder in RDP
sessions entirely.

## Test Inventory

### macOS Tests

Command:

```bash
cd src/macos
swift test
```

Current suite: **319 passing, 1 skipped**. The skip is
`testEmitMacOSFixtureForCrossPlatformDecode`, a fixture generator rather than an
assertion; it runs only with `LANMSG_EMIT_H264_FIXTURE` set.

Coverage:

- `ConfigStoreTests`: filename sanitization, config coding, pending message and
  contact serialization.
- `CryptoTests`: X25519 symmetry, text AES-GCM round trips, bad AAD/ciphertext,
  known vectors, history crypto.
- `FrameCodecTests`: frame round trips, big-endian length, known frame,
  oversize rejection.
- `HistoryStoreTests`: encrypted history round trip, cap enforcement, wrong key
  behavior, known history vector.
- `MessageStatusTests`: monotonic status ranking and history status update.
- `NetworkInterfaceMonitorTests`: adapter filtering, broadcast calculation,
  idempotent start, observer behavior.
- `PacketValidatorTests`: packet validation, self suppression, nonce checks, file
  size checks, filename sanitization.
- `PresenceEvaluatorTests`: LAN presence state-machine transitions.
- `MessageEditTests` / `RelayControlTests`: edit and delete rules, the
  `requireIncoming` security gate, and the relay control envelope.
- `DiscoveryServiceQueueTests`: the discovery threading invariant — the blocking
  receive loop must not starve the beacon timer, the socket rebuild, or the
  health summary.
- `DockPolicyGuardTests`, `AttachmentPasteboardTests`, `NetLoggerTests`,
  `StressTests`.
- Remote desktop: `RemoteSessionCryptoTests` (32), `MediaFrameTests` (26),
  `H264BitstreamTests` (15), `H264EncoderTests` (12), `H264DecoderTests` (14),
  `SampleBufferVideoPresenterTests` (3), `ScreenCaptureSourceTests` (18),
  `VideoPipelineEndToEndTests` (5), `ProtocolCapabilityTests` (10),
  `PeerKeyTrustTests` (9), `RemoteDesktopQueueTests` (4).

### Windows Tests

Command:

```powershell
cd src\windows-native
$testDll = Get-ChildItem LanMessenger.Tests\bin -Filter LanMessenger.Tests.dll -Recurse | Select-Object -First 1
dotnet vstest $testDll.FullName --logger:"console;verbosity=normal"
```

Current suite: **210 passing**, last run on real Windows hardware 2026-09-14.

Coverage mirrors the macOS areas: config, crypto, frame codec, history, message
status, network interface monitoring, packet validation, presence, message
editing, relay control, clipboard attachments, and the remote-desktop suites
(`RemoteSessionCryptoTests` 34, `MediaFrameTests` 26, `H264BitstreamTests` 15,
`RemoteDesktopQueueTests` 4).

One known result: `PacketValidatorTests.SanitizeFilenameStripsPath` **fails on
macOS and passes on Windows**, because `SanitizeFilename` uses
`Path.DirectorySeparatorChar`. On Windows it should pass; if it does not, that
is a real regression.

### Test Vectors

Four fixtures are carried in **both** test directories, and must stay
byte-for-byte equivalent:

| Fixture | Covers |
|---|---|
| `known_good_exchange.json` | Text encryption, file chunk encryption, history encryption |
| `remote_handshake_vector.json` | The remote-desktop media handshake |
| `media_frame_vector.json` | Media frame header, AAD and sealed payload |
| `windows_h264_sample.h264` | Real Microsoft H264 Encoder MFT output — 60 frames, 126 NAL units, 129,547 bytes |

`windows_h264_sample.h264` **cannot be regenerated without the Windows machine**.
Do not delete it. The macOS counterpart is deliberately not committed because it
regenerates in a second on any Mac:

```bash
cd src/macos
LANMSG_EMIT_H264_FIXTURE=/tmp/macos_sample.h264 \
  swift test --filter testEmitMacOSFixtureForCrossPlatformDecode
```

A change to any shared fixture is a change to both copies. CLAUDE.md's
validation checklist calls this out because updating one is the natural mistake.

## Validation By Change Type

| Change type | Minimum validation |
|---|---|
| Docs only | `git diff --check`; grep for stale claims |
| Swift app code | `cd src/macos && swift build && swift test` |
| macOS packaging | `scripts/macos/package.sh` and relevant validate/smoke scripts |
| C#/WinUI code | MSBuild restore/build and MSTest on Windows |
| Windows packaging | MSBuild publish, Inno Setup, smoke test |
| Protocol/crypto/framing | Both platform tests and protocol docs |
| Remote desktop | Both platform tests, both copies of every shared fixture, and `docs/REMOTE_DESKTOP.md` if status changed |
| Discovery/networking | Platform test where possible plus runtime LAN test |
| Updates/release | Workflow review, updater docs, artifact naming/sidecar check |

If a target platform is not available locally, say so in the final handoff and
identify the exact CI or machine validation still needed.

## Versioning

Canonical versions:

- `version/macos.json`
- `version/windows.json`

Install the pre-commit hook:

```bash
cp scripts/hooks/pre-commit .git/hooks/pre-commit
chmod +x .git/hooks/pre-commit
```

Default bump is patch. Override:

```bash
BUMP=minor git commit -m "feat: ..."
BUMP=major git commit -m "release: ..."
```

The hook:

- bumps `version/macos.json` when staged files are under `src/macos/`;
- syncs macOS `MARKETING_VERSION` in `src/macos/project.yml`;
- bumps `version/windows.json` when staged files are under `src/windows-native/`;
- syncs Windows `<Version>` in `LanMessenger.csproj`;
- skips files staged only under `version/`.

Legacy `src/macos/VERSION` and `src/windows-native/VERSION` are not release
sources of truth.

## Protocol Change Workflow

1. Update [../PROTOCOL.md](../PROTOCOL.md).
2. Update Swift and C# packet definitions.
3. Update validation in both implementations.
4. Update service logic in both implementations.
5. Update history/config decoding defaults if needed.
6. Update or add tests on both platforms.
7. Update test vectors if encryption/framing bytes change.
8. Update [ARCHITECTURE.md](ARCHITECTURE.md) and [FILE_MAP.md](FILE_MAP.md) when
   file responsibilities or flows change.

New fields should be optional unless you are deliberately breaking compatibility.

## UI Change Workflow

macOS:

- Prefer SwiftUI components under `UI/`.
- Keep AppKit escape hatches small and documented.
- Preserve menu-bar and dock behavior.
- Keep long-running file/network work off the main actor.

Windows:

- Prefer WinUI controls and code-behind patterns already in `UI/`.
- Keep dialog ownership in `MainWindow.xaml.cs` because only one ContentDialog
  can be open per XamlRoot.
- Preserve tray lifecycle and close-to-tray semantics.
- Avoid full collection refreshes for per-message status updates when targeted
  row updates exist.

## Networking Change Workflow

Before changing discovery:

- Verify UDP packets remain raw JSON.
- Keep per-interface send sockets.
- Keep multicast joins per interface.
- Keep self-suppression by own public key and own IP.
- Keep discovery replies on UDP `54231`.
- Consider VPN/virtual adapter behavior.

Before changing TCP:

- Keep the 4-byte big-endian frame prefix.
- Keep max frame rejection.
- Do not block UI threads on socket writes.
- Preserve receipt behavior on successful decrypt.

## File Transfer Change Workflow

Preserve these contracts:

- one TCP connection per file transfer;
- `file_start` -> ordered encrypted chunks -> `file_end`;
- 64 KiB plaintext chunks;
- `transfer_id` as AAD;
- temp `.part` file until finalization;
- dedup final filenames;
- progress throttling.

## Update/Release Change Workflow

When changing asset names, tags, packaging formats, or updater behavior, update:

- platform build workflow;
- `release.yml`;
- platform `UpdateService`;
- [RELEASE_AND_OPERATIONS.md](RELEASE_AND_OPERATIONS.md);
- smoke tests if install paths or launch behavior changed.

Keep sidecar hashes attached to platform releases even if the combined release
only exposes end-user installers.

## Diagnostic Logging

Both clients write a structured log to disk for support and bug-report use.
Each line is `[yyyy-MM-dd HH:mm:ss.fffZ] LEVEL Category: message`. Levels are
DEBUG, INFO, WARN, ERROR, CRIT. Every level always writes: there is
deliberately no verbose toggle, because a diagnostic level that is off by
default is off exactly when a user hits the bug you needed it for. Volume is
bounded by rotation instead.

Locations:

- macOS: `~/Library/Application Support/LanMessenger/Logs/client.log`
- Windows: `%APPDATA%\LanMessenger\Logs\client.log`

Rotation: the active log caps at 5 MiB. On overflow it is gzipped to
`client.1.log.gz`; prior archives shift up to `client.4.log.gz`. The oldest
is deleted. Each fresh file begins with a `# Session` line containing the
OS version, app version, architecture, and hostname.

Structured event helpers exist for the high-value paths:

- `NetLogger.fileTransfer / LanLogger.FileTransfer` — emits
  `event=...` with `transfer_id`, `peer`, `dir`, `file`, `size`, `mime`,
  `sent`, `recv`, `ms`, `bps`, `retries`, `reason` when relevant.
- `NetLogger.screenshot / LanLogger.Screenshot` — emits `event=...` with
  `display`, `res`, `perm`, `init_ms`, `interrupt`, `path`.
- `NetLogger.peer / LanLogger.Peer` — emits `event=...` with `peer`,
  `pubkey` (first 8 chars), `ms`, `reason`. Every presence transition
  (online/probing/offline) is logged here with the quiet time that caused it,
  because presence drives queueing and relay routing.
- `NetLogger.update / LanLogger.Update` — update checks, downloads, installs
  (→ `update.log`).
- `NetLogger.backend / LanLogger.Backend` — any outbound server call (cloud
  relay, GitHub release API) with `service`, `op`, `http`, `ms`, `count`.
- `NetLogger.crash / LanLogger.Crash` — fatal diagnostics (→ `crash.log` and
  `client.log`). Written synchronously, because the async path never drains
  when the process is about to die.

Each channel writes its own file (`client`, `transfer`, `screenshot`,
`discovery`, `peer`, `crypto`, `ui`, `retry`, `update`, `crash`, `remote`), and
all of them are rotated and included in the export bundle.

`remote` is the remote-desktop channel. It exists and is exported, but nothing
writes to it yet — it was added early on purpose, because the export bundle is
derived from the `LogChannel` enum and a channel missing from that enum silently
never reaches a bug report. Channel-coverage tests on both platforms enumerate
the enum rather than a hardcoded list.

### Discovery health summary

Discovery emits one summary line per minute on both platforms:

```text
Discovery: health window=60s tx_beacons=40 tx_replies=38 tx_failures=0 \
  rx=[discovery=39,discovery_reply=40] send_sockets=1 recv_socket=1 interfaces=1
```

`tx_beacons=0` or `rx=[none]` additionally logs a WARN naming the likely cause
(starved beacon timer, or inbound UDP blocked). This exists because a real
incident — beacons silently stopping while replies kept working — was only
confirmable by hand-counting tens of thousands of log lines across two
machines. Do not remove it.

### Crash capture

Both platforms record abnormal termination. macOS installs
`NSSetUncaughtExceptionHandler` plus handlers for SIGSEGV/SIGBUS/SIGILL/
SIGFPE/SIGABRT/SIGTRAP (Swift runtime traps such as a nil force-unwrap or
`fatalError` raise a signal, not an exception, so both hooks are needed) and
drops a `.running` marker that is cleared on clean shutdown, so the next launch
can report that the previous run died. Windows hooks
`Application.UnhandledException`, `AppDomain.CurrentDomain.UnhandledException`,
and `TaskScheduler.UnobservedTaskException`.

Always use the structured helpers for new high-value events; free-form
`info/warn/error` calls are fine for one-off diagnostics that don't need a
canonical schema.

Users can attach logs to a bug report from Settings → Logging:

- "Open Logs Folder" opens the directory in Finder/Explorer.
- "Export Logs…" bundles the active log and every rotated archive into a
  single .zip the user can drop into an email or GitHub issue.

CI uploads `crash-reports/client-logs/` on smoke-test failure for both
platforms, so a failed build run includes the same structured trail.

## Local Runtime State

Do not commit local app state. Ignored paths include:

- `dist/`
- `builds/`
- `releases/`
- `Logs/`
- `.lan_messenger/`
- generated `src/macos/LanMessenger.xcodeproj/`
- Swift build output under `src/macos/.build/` and `src/macos/build/`

## Documentation Maintenance

Docs are part of the deliverable. Update them when:

- a command changes;
- a file moves;
- a service takes on a new responsibility;
- a packet/config/history field changes;
- a CI or packaging workflow changes;
- a known gotcha is discovered;
- local memory files are stale;
- the remote-desktop status changes — [REMOTE_DESKTOP.md](REMOTE_DESKTOP.md) is
  the handoff document for that feature and goes stale fastest.
