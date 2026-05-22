# LAN Messenger

LAN Messenger is a native, peer-to-peer chat app for people on the same local
network. It has no accounts, no central server, and no cloud relay. macOS and
Windows builds discover each other over the LAN, exchange X25519 public keys,
and send encrypted messages and files directly over TCP.

## Current Platforms

| Platform | Stack | Location | Status |
|---|---|---|---|
| macOS | Swift 5.9, SwiftUI, Swift Package Manager, XcodeGen for app packaging | `src/macos/` | Active |
| Windows | C#/.NET 8, WinUI 3, Windows App SDK, Inno Setup | `src/windows-native/` | Active |

Both apps speak the same wire protocol. Any networking, crypto, framing,
receipt, or file-transfer change must be checked against [PROTOCOL.md](PROTOCOL.md)
and the platform test vectors.

## Documentation Map

Start here when working on the repo:

- [CLAUDE.md](CLAUDE.md) - agent/developer operating guide with build commands,
  protocol rules, repo conventions, and gotchas.
- [PROTOCOL.md](PROTOCOL.md) - authoritative wire protocol and persistence
  compatibility spec.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) - end-to-end architecture, data
  flows, storage, services, UI, and update system.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) - local setup, test commands,
  validation, versioning, and change workflow.
- [docs/RELEASE_AND_OPERATIONS.md](docs/RELEASE_AND_OPERATIONS.md) - CI,
  packaging, releases, updaters, diagnostics, and incident handling.
- [docs/FILE_MAP.md](docs/FILE_MAP.md) - file-by-file repository inventory.
- [memory/](memory/) - repo-local memory for future sessions. These files are
  documentation, not application runtime state.

## Features

- Zero-config peer discovery over UDP broadcast, multicast, and saved-peer
  unicast hints.
- Direct TCP messaging on a fixed LAN port.
- End-to-end encrypted text messages using X25519, HKDF-SHA256, and
  AES-256-GCM.
- Encrypted file transfer using a separate TCP connection per transfer.
- Encrypted local history, capped to 200 messages per conversation.
- Saved contacts, contact photos, hidden/deleted conversations, archive/unarchive,
  and "new message" flows.
- Typing indicators, sent receipts, read receipts, and WhatsApp-style status
  icons.
- Reply metadata for native clients, sent as optional top-level fields for
  backward compatibility.
- Offline pending text and file queues that drain when a saved peer reappears.
- Native notifications and tray/menu-bar lifecycle.
- In-app update checks from GitHub Releases.

## Quick Start

### macOS

Requires macOS 13+ and Xcode command line tools.

```bash
cd src/macos
swift build
swift run
swift test
```

The repo does not commit an Xcode project. Generate one when needed:

```bash
cd src/macos
xcodegen generate
open LanMessenger.xcodeproj
```

Build local installable artifacts through the same script CI uses:

```bash
VERSION=$(jq -r '.version' ../../version/macos.json) ../../scripts/macos/package.sh
```

For the faster local wrapper:

```bash
cd src/macos
./scripts/build_app.sh
```

### Windows

Requires Windows 10 build 19041+, Visual Studio 2022 with WinUI/Windows App SDK
support, .NET 8, and x64.

```powershell
cd src\windows-native
msbuild /t:Restore /p:Configuration=Release /p:Platform=x64 LanMessenger.sln
msbuild LanMessenger.Tests\LanMessenger.Tests.csproj /p:Configuration=Release /p:Platform=x64
dotnet vstest (Get-ChildItem LanMessenger.Tests\bin -Filter LanMessenger.Tests.dll -Recurse | Select-Object -First 1).FullName
```

For a local app build:

```powershell
msbuild LanMessenger\LanMessenger.csproj /t:Publish /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=true
```

## Tests

| Platform | Command | Coverage focus |
|---|---|---|
| macOS | `cd src/macos && swift test` | 52 test methods across framing, validation, crypto, history, config, message status, and interface monitoring |
| Windows | `cd src/windows-native && dotnet vstest <test dll>` | 45 MSTest methods covering the same protocol and persistence contracts |

Both suites include `known_good_exchange.json`. Keep the macOS and Windows copies
in sync whenever the protocol test vectors change.

## Runtime Ports

| Purpose | Protocol | Port |
|---|---|---|
| Discovery | UDP raw JSON | `54231` |
| Messages and file-transfer frames | TCP length-prefixed JSON | `54232` |

The app is LAN-only. It does not require internet access except for optional
GitHub release update checks.

## Storage Locations

| Data | macOS | Windows |
|---|---|---|
| Config | `~/Library/Application Support/LanMessenger/config.json` | `%APPDATA%\LanMessenger\config.json` |
| Private key | Keychain service `com.dave.lanmessenger`, account `privateKey` | DPAPI-protected `%APPDATA%\LanMessenger\private.key.dpapi` |
| History | `~/Library/Application Support/LanMessenger/history.enc` | `%APPDATA%\LanMessenger\history.enc` |
| Received files | Configured inbox or `Received/` under app data | Configured inbox or `Received\` under app data |
| Logs | `~/Library/Application Support/LanMessenger/Logs/client.log` (5 MiB rolling, gzipped archives) | `%APPDATA%\LanMessenger\Logs\client.log` (5 MiB rolling, gzipped archives) |
| Update staging | `~/Library/Application Support/LanMessenger/Updates/` | `%APPDATA%\LanMessenger\Updates\` |

## Diagnostics

If something misbehaves, open Settings → Logging:

- "Open Logs Folder" — opens the client log directory in Finder / Explorer.
- "Export Logs…" — packages the active log and every rotated `.log.gz`
  archive into a single zip you can attach to a bug report.
- "Verbose logging" toggle — turn this on before reproducing a transfer
  or screen-capture problem; it records DEBUG-level events (per-chunk
  progress, decrypt outcomes) the standard log omits.

Each line carries a millisecond-precision UTC timestamp, a level
(`DEBUG`/`INFO`/`WARN`/`ERROR`/`CRIT`), and a category. File transfers,
screenshots, and peer-connection events emit structured `key=value`
tails (e.g. `event=complete transfer_id=… size=… bps=…`) so they're
easy to grep without writing a parser.

## Versioning

The canonical version files are:

- `version/macos.json`
- `version/windows.json`

The pre-commit hook at [scripts/hooks/pre-commit](scripts/hooks/pre-commit)
bumps platform versions when platform source files are staged. Install it locally
with:

```bash
cp scripts/hooks/pre-commit .git/hooks/pre-commit
chmod +x .git/hooks/pre-commit
```

`src/macos/VERSION` and `src/windows-native/VERSION` are legacy markers and are
not used by the CI release pipelines.

## Repository Layout

```text
.
|-- README.md
|-- CLAUDE.md
|-- PROTOCOL.md
|-- docs/
|   |-- ARCHITECTURE.md
|   |-- DEVELOPMENT.md
|   |-- FILE_MAP.md
|   `-- RELEASE_AND_OPERATIONS.md
|-- memory/
|-- scripts/
|-- version/
`-- src/
    |-- macos/
    `-- windows-native/
```

Use [docs/FILE_MAP.md](docs/FILE_MAP.md) for the detailed file-by-file map.

## License

MIT
