---
name: Swift build gotchas for LAN Messenger native
description: macOS Swift/SPM/XcodeGen patterns and compiler gotchas for this repo
type: project
---

Current macOS source root: `src/macos/`.

## Build Paths

- Fast dev path: `cd src/macos && swift build && swift test`.
- Xcode project is generated from `src/macos/project.yml` with `xcodegen generate`.
- Generated `LanMessenger.xcodeproj` is ignored and should not be treated as
  durable source.
- Packaging path: root `scripts/macos/package.sh`.

## Compiler And Runtime Gotchas

1. `bind` name collision:
   - Classes with NSObject context must call `Darwin.bind(...)` for POSIX bind.

2. `InputStream.read` pointer arithmetic:
   - Use `withUnsafeMutableBytes` and `advanced(by:)` for offset reads.

3. Main actor callbacks:
   - Background networking callbacks should hop to the main actor with
     `Task { @MainActor ... }` or `DispatchQueue.main.async`.

4. `ConfigStore.config` mutability:
   - It is intentionally mutable because services update nested struct fields.

5. Filename sanitization:
   - Use string splitting for POSIX semantics. `URL(fileURLWithPath: "")` can
     produce current-directory behavior and is not equivalent.
   - Strip null bytes before path handling.

6. Test vectors:
   - SPM test resources must live inside the package tree. The current vector
     path is `src/macos/LanMessengerTests/known_good_exchange.json`.

7. Swift concurrency:
   - Keep blocking socket and file I/O off the main actor and off cooperative
     async paths where possible. Dedicated dispatch queues are used for file
     chunks and file sends.

8. Packaging:
   - `project.yml` stamps `MARKETING_VERSION` as `MAJOR.MINOR`; full versions
     come from `version/macos.json` in CI/package scripts.
   - The package script regenerates the Xcode project and builds with xcodebuild.
