---
name: Swift build gotchas for LAN Messenger native
description: Swift compiler issues discovered and fixed during Phase 2 — important for future sessions editing the macOS source
type: project
---

Discoveries from getting `swift build` and `swift test` clean on the macOS SPM package.

**Why:** These were real compiler errors that needed specific fixes; future sessions editing these files should know the patterns.

**How to apply:** When editing networking or crypto code, watch for these patterns.

## 1. `bind` name collision in NetworkCoordinator and DiscoveryService

`NetworkCoordinator` and `DiscoveryService` extend `NSObject` (or are in a context where `bind(_:_:_:)` is an instance method). When calling the POSIX `bind()` syscall inside these classes, must use `Darwin.bind(...)` to disambiguate.

## 2. InputStream read with pointer arithmetic

`stream.read(&buffer + offset, maxLength: n)` is illegal — produces a temporary pointer. Must use:
```swift
buffer.withUnsafeMutableBytes { ptr in
    stream.read(ptr.baseAddress!.advanced(by: offset).assumingMemoryBound(to: UInt8.self), maxLength: n)
}
```
This applies to both `FrameCodec.readExact` and `PeerSession.tryReadExact`.

## 3. @MainActor protocol + nonisolated call sites

`NetworkCoordinatorDelegate` was first annotated `@MainActor` on the protocol, which caused errors in non-isolated call sites. Solution: annotate each method `@MainActor` individually, and call them via `Task { @MainActor [weak self] in ... }` from background threads/queues.

## 4. ConfigStore.config must be var (not private(set) var)

MessagingService directly mutates `ConfigStore.shared.config.pendingMessages`. Using `private(set)` prevents sub-property mutation on a struct value type. Changed to plain `var config`.

## 5. Filename sanitization: use string splitting not URL

`URL(fileURLWithPath: "")` returns the current directory on macOS (not `""`). The correct POSIX-equivalent of Python's `Path(name).name` is:
```swift
name.components(separatedBy: "/").last ?? ""
```
Also: null bytes in filenames must be removed with `.replacingOccurrences(of: "\0", with: "")` BEFORE passing through URL (URL percent-encodes them as `%00`).

## 6. Test vectors must be inside the package directory

SPM `.copy("../../../test_vectors/...")` resources are rejected if outside the package root. Solution: copy `known_good_exchange.json` into `LanMessengerTests/` and reference it as `.copy("known_good_exchange.json")`. Both locations are kept in sync.

## 7. Swift 6 Sendable warnings (not errors yet)

Several warnings about captured vars in concurrently-executing closures (FileTransferService). These are warnings in Swift 5.9 mode but will become errors in Swift 6. Acceptable for now; fix in a later pass when migrating to Swift 6 concurrency.
