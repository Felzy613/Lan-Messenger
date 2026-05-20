# Release And Operations

This document describes build pipelines, release layout, updater behavior,
runtime diagnostics, and operational recovery steps.

## Release Model

LAN Messenger publishes platform pre-releases first, then a combined release.

| Release type | Tag format | Assets |
|---|---|---|
| macOS platform | `macos-vX.Y.Z` | DMG, ZIP, PKG, SHA256 sidecars |
| Windows platform | `windows-vX.Y.Z` | Inno Setup EXE, SHA256 sidecar |
| Combined public release | `release-winX.Y.Z-macA.B.C` | end-user DMG and EXE only |

The combined release keeps the public downloads page clean. In-app updaters can
still find update-channel ZIP/EXE assets and sidecars on the platform releases.

## Version Sources

Canonical:

- `version/macos.json`
- `version/windows.json`

Synchronized outputs:

- macOS `src/macos/project.yml` uses `MARKETING_VERSION` as `MAJOR.MINOR`.
- Windows `src/windows-native/LanMessenger/LanMessenger.csproj` uses
  `<Version>X.Y.Z</Version>`.

Legacy markers:

- `src/macos/VERSION`
- `src/windows-native/VERSION`

Those legacy files are not used by release CI.

## GitHub Actions

### PR Checks

Workflow: `.github/workflows/pr-checks.yml`

Jobs:

- `test-macos`: runs `swift test` on `macos-15`.
- `test-windows`: restores, builds the test project, and runs `dotnet vstest`
  on `windows-2022`.
- `comment`: posts or updates a single PR summary comment.

### Build macOS

Workflow: `.github/workflows/build-macos.yml`

Trigger:

- push to `main` touching macOS sources, macOS version, shared files, macOS
  scripts, workflow files, actions, or `Images/Logo.png`;
- manual dispatch.

Pipeline:

1. Preflight validates `version/macos.json` and checks for an existing
   `macos-vX.Y.Z` release.
2. Build job runs Swift tests.
3. Icon catalog and `AppIcon.icns` are regenerated from `Images/Logo.png`.
4. Optional signing certificate is imported.
5. `scripts/macos/package.sh` builds DMG, ZIP, PKG, and SHA256 sidecars.
6. Artifacts are uploaded for validation.
7. Validation verifies SHA256 sidecars.
8. `validate-dmg.sh` mounts and validates the DMG.
9. `smoke-test.sh` install-launch tests DMG and ZIP unless skipped.
10. Platform release `macos-vX.Y.Z` is published as a pre-release.
11. Resolved CI issues for macOS are auto-closed.

Manual inputs:

- `force_rebuild`
- `skip_smoke`
- `notarize`

### Build Windows

Workflow: `.github/workflows/build-windows.yml`

Trigger:

- push to `main` touching Windows sources, Windows version, shared files,
  workflow/action files, or Windows scripts;
- manual dispatch.

Pipeline:

1. Preflight validates `version/windows.json` and checks for an existing
   `windows-vX.Y.Z` release.
2. Restore dependencies with MSBuild.
3. Build and run tests.
4. Publish self-contained x64 app.
5. Copy VC++ runtime DLLs app-local.
6. Download `vc_redist.x64.exe` for installer chaining.
7. Build Inno Setup installer and SHA256 sidecar.
8. Startup smoke test installs and launches the EXE.
9. Platform release `windows-vX.Y.Z` is published as a pre-release.
10. Resolved CI issues for Windows are auto-closed.

Manual inputs:

- `force_rebuild`
- `skip_smoke`

### Release Orchestration

Workflow: `.github/workflows/release.yml`

Trigger:

- successful `Build macOS` or `Build Windows` workflow on `main`;
- manual dispatch.

Behavior:

- serializes release creation with a concurrency group;
- exits early if the combined release already exists;
- waits up to 45 minutes for a sibling platform build for the same commit;
- finds latest `macos-v*` and `windows-v*` releases;
- creates a draft combined release;
- uploads only the DMG and EXE;
- publishes by removing draft status;
- deletes failed draft releases as a safety net.

### Integrity Check

Workflow: `.github/workflows/integrity-check.yml`

Runs every Monday at 08:00 UTC and on manual dispatch.

It verifies:

- version files exist and contain semver;
- latest platform releases exist;
- platform releases have assets;
- a combined release exists.

On failure, it creates or updates an issue labeled `ci-failure` and
`type:integrity`.

## Composite Actions

### report-failure

Path: `.github/actions/report-failure/action.yml`

Creates or updates CI failure issues. It:

- extracts error-like lines from a log;
- creates a stable SHA256 fingerprint;
- ensures labels exist;
- comments on matching open issues;
- creates a new issue when no fingerprint match exists.

### validate-version

Path: `.github/actions/validate-version/action.yml`

Reads `version/{platform}.json`, validates semver, and checks whether the
platform release tag already exists.

This action exists but platform workflows also contain inline preflight logic.

## macOS Packaging Details

Canonical script: `scripts/macos/package.sh`

Outputs:

- `LanMessenger-macOS-X.Y.Z.dmg`
- `LanMessenger-macOS-X.Y.Z.dmg.sha256`
- `LanMessenger-macOS-X.Y.Z.zip`
- `LanMessenger-macOS-X.Y.Z.zip.sha256`
- `LanMessenger-macOS-X.Y.Z.pkg`
- `LanMessenger-macOS-X.Y.Z.pkg.sha256`

Main stages:

1. Resolve repo and build paths.
2. Generate Xcode project with XcodeGen.
3. Build Release with xcodebuild.
4. Copy the app from DerivedData.
5. Code-sign ad-hoc or with Developer ID.
6. Optionally notarize.
7. Stage `LAN Messenger.app`.
8. Build update ZIP with `ditto --keepParent`.
9. Build drag-to-Applications DMG.
10. Optionally sign/notarize DMG.
11. Build PKG with preinstall/postinstall scripts.
12. Write SHA256 sidecars.

Local wrapper scripts:

- `src/macos/scripts/build_app.sh`
- `src/macos/scripts/build_dmg.sh`

Both wrappers read `version/macos.json`, default to `SKIP_PKG=1`, and delegate to
the canonical package script.

## macOS Validation

### validate-bundle.sh

Checks:

- `Contents/Info.plist`;
- executable path and bit;
- required Info.plist keys;
- app icon resources;
- icon plist references;
- code signing display and deep verification.

### validate-dmg.sh

Checks:

- `hdiutil verify`;
- DMG mounts;
- root `.app` exists;
- `/Applications` symlink exists and points correctly;
- optional volume icon;
- embedded app passes `validate-bundle.sh`.

### smoke-test.sh

Supports `.dmg`, `.pkg`, and `.zip`.

It:

- installs to `/Applications/LAN Messenger.app`;
- validates the installed bundle;
- clears quarantine;
- launches with `open`;
- waits for `LanMessenger` process;
- holds a stability window;
- captures crash report excerpts on failure;
- cleans up unless `--keep-installed` is passed.

## Windows Packaging Details

Primary files:

- `src/windows-native/LanMessenger.csproj`
- `src/windows-native/LanMessenger.iss`
- `.github/workflows/build-windows.yml`

The project is unpackaged WinUI 3, self-contained, x64-only.

Important project behavior:

- `WindowsPackageType=None`.
- `WindowsAppSDKSelfContained=true`.
- `IncludePriFileInPublishOutput` copies generated `.pri` resources into
  publish output to prevent runtime XAML load failures.
- Assets are copied to output.
- `NSec.Cryptography` and Windows App SDK dependencies come from NuGet.

Installer behavior:

- Installs under Program Files.
- Offers desktop icon.
- Offers startup entry.
- Installs VC++ redistributable if needed.
- Adds firewall allow rules for UDP 54231 and TCP 54232 on private/domain
  profiles.
- Starts the app after install and relaunches during silent updater installs.
- Removes firewall rules on uninstall.

## Windows Smoke Test

Script: `scripts/windows/smoke-test.ps1`

It:

- silently installs the Inno EXE;
- waits up to 3 minutes for installer completion;
- finds `LanMessenger.exe`;
- launches the app;
- waits for startup;
- holds a stability window;
- collects Event Viewer errors and crash dump names on early exit;
- closes or kills the process at the end.

## In-App Updater Operations

### macOS

Updater service: `src/macos/LanMessenger/Core/Services/UpdateService.swift`

Checks GitHub releases and chooses a macOS ZIP. It verifies size and SHA256 when
a sidecar exists. It writes a helper shell script to the update staging directory.
The helper waits for the current PID to exit, moves the old app aside, installs
the new app with `ditto`, clears quarantine, verifies codesign best-effort,
re-registers Launch Services, relaunches, and deletes backup on success.

Logs:

```text
~/Library/Application Support/LanMessenger/Logs/update.log
```

or the configured logs directory from `ConfigStore`.

### Windows

Updater service:
`src/windows-native/LanMessenger/Core/Services/UpdateService.cs`

Checks GitHub releases and chooses a Windows installer EXE. It verifies size and
SHA256 when a sidecar exists. It uses a staging lock to prevent concurrent
installs, kills other `LanMessenger` processes, removes Zone.Identifier, launches
the installer elevated with silent flags, and exits the current process.

Logs:

```text
%APPDATA%\LanMessenger\Logs\update.log
```

## Runtime Diagnostics

### macOS

Network log:

```text
~/Library/Application Support/LanMessenger/Logs/client.log
```

Use Console.app or:

```bash
log show --process LanMessenger --last 5m --info
```

Crash reports:

```text
~/Library/Logs/DiagnosticReports/LanMessenger*
```

### Windows

Network log:

```text
%APPDATA%\LanMessenger\Logs\client.log
```

Update log:

```text
%APPDATA%\LanMessenger\Logs\update.log
```

Startup crash log:

```text
%APPDATA%\LanMessenger\crash.log
```

Event Viewer:

```powershell
Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=(Get-Date).AddMinutes(-10); Level=1,2 }
```

Crash dumps:

```text
%LOCALAPPDATA%\CrashDumps\LanMessenger*
```

## Operational Triage

### Peers Not Appearing

Check:

- app is running on both devices;
- same LAN or routed subnet;
- UDP 54231 allowed through firewall;
- Windows installer firewall rules exist;
- logs show eligible interfaces;
- logs show discovery sends and receives;
- source IP is not being self-suppressed due stale interface list.

Relevant files:

- macOS `Core/Networking/DiscoveryService.swift`
- Windows `Core/Networking/DiscoveryService.cs`
- `NetworkInterfaceMonitor` on both platforms

### Messages Stay Queued Or At One Check

Check:

- receiver log for decrypt failure;
- sender log for `FireTcp` failure;
- public keys match the saved contact;
- receiver sent `sent_receipt`;
- status ranking did not reject the update unexpectedly;
- both clients can reach TCP 54232.

Relevant files:

- `MessagingService`
- `SessionCrypto`
- `MessageStatus`
- `HistoryStore`

### File Transfers Fail

Check:

- file still exists for pending offline files;
- inbox directory exists and is writable;
- enough disk space;
- chunks are processing in order;
- progress events are throttled;
- receiver finalized `.part` file on `file_end`.

Relevant files:

- `FileTransferService`
- `FileTransferStore`
- `PacketValidator.sanitizeFilename`

### macOS Package Fails Validation

Check:

- `src/macos/build/package.log`;
- generated Xcode project exists during package run;
- app bundle has `Info.plist`, executable, and icon resources;
- code signing identity is present or ad-hoc mode is expected;
- `validate-bundle.sh` output;
- `validate-dmg.sh` output.

### Windows Startup Crash After Install

Check:

- `%APPDATA%\LanMessenger\crash.log`;
- Event Viewer;
- app-local `libsodium.dll`;
- app-local `vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`;
- `.pri` file in publish output;
- Windows App SDK runtime behavior.

### Updater Fails

Check:

- release assets and sidecars;
- selected asset name;
- SHA256 sidecar content format;
- file size thresholds;
- staging directory permissions;
- macOS helper script log or Windows installer log;
- whether the current app can terminate.

## Manual Release Checklist

1. Verify `version/macos.json` and `version/windows.json`.
2. Confirm platform source changes were committed with version sync.
3. Run or trigger platform workflows.
4. Wait for platform pre-releases.
5. Confirm combined release is created and contains exactly one DMG and one EXE.
6. Confirm platform releases retain update-channel artifacts and SHA256 sidecars.
7. Install both artifacts on clean machines when feasible.
8. Test discovery, text, receipts, file transfer, close-to-tray/menu-bar, and
   update check.

## CI Issue Handling

CI failures are intentionally persisted as GitHub issues through
`report-failure`. When a later platform build and smoke test succeeds,
`scripts/shared/close-ci-issues.sh` comments on and closes matching open issues.

Do not delete failure issues manually unless they are duplicates without a useful
fingerprint. The recurrence trail is useful for release health.
