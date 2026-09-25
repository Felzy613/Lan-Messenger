import Foundation

/// Keeps a test run out of the user's real files.
///
/// The suite exercises the app's singletons directly, as the same user as the
/// real app, so anything that resolves a path under Application Support reads
/// and writes the live app's files unless it asks here first. Both times that
/// was left to chance it bit: a thread under `192.168.99.77`, sender "me",
/// dated 1970, sat in a real history file until the user hid it; and every
/// `swift test` appended its own `# Session` header and `typing from
/// 192.168.1.31 dropped` lines to the running app's `client.log` — the file a
/// user attaches to a bug report. XCTest is never linked into the app, so its
/// presence is an unambiguous signal.
///
/// Every persistence singleton — `HistoryStore`, `ConfigStore`, `NetLogger` —
/// resolves its location through this. A new one must too.
enum TestIsolation {

    static let isActive: Bool = NSClassFromString("XCTestCase") != nil

    /// A per-process path in the temp directory:
    /// `lanmessenger-tests-<pid>-<name>`. Per process so two concurrent runs
    /// never share a history file or interleave a log.
    static func scratchURL(_ name: String) -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("lanmessenger-tests-\(ProcessInfo.processInfo.processIdentifier)-\(name)")
    }
}
