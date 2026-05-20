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

## Test Inventory

### macOS Tests

Command:

```bash
cd src/macos
swift test
```

Current suite: 52 Swift test methods.

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

### Windows Tests

Command:

```powershell
cd src\windows-native
$testDll = Get-ChildItem LanMessenger.Tests\bin -Filter LanMessenger.Tests.dll -Recurse | Select-Object -First 1
dotnet vstest $testDll.FullName --logger:"console;verbosity=normal"
```

Current suite: 45 MSTest methods.

Coverage mirrors the macOS areas: config, crypto, frame codec, history, message
status, network interface monitoring, and packet validation.

### Test Vectors

Both platforms carry `known_good_exchange.json`:

- `src/macos/LanMessengerTests/known_good_exchange.json`
- `src/windows-native/LanMessenger.Tests/known_good_exchange.json`

Keep them byte-for-byte equivalent if updated. They cover text encryption, file
chunk encryption, and history encryption.

## Validation By Change Type

| Change type | Minimum validation |
|---|---|
| Docs only | `git diff --check`; grep for stale claims |
| Swift app code | `cd src/macos && swift build && swift test` |
| macOS packaging | `scripts/macos/package.sh` and relevant validate/smoke scripts |
| C#/WinUI code | MSBuild restore/build and MSTest on Windows |
| Windows packaging | MSBuild publish, Inno Setup, smoke test |
| Protocol/crypto/framing | Both platform tests and protocol docs |
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
- local memory files are stale.
