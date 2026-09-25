import XCTest
@testable import LanMessenger

// The suite runs as the same user as the installed app, so a path that
// resolves to Application Support is the live app's file. Both the history
// file and client.log were written by `swift test` before every persistence
// singleton asked TestIsolation where to go. Mirrors TestIsolationTests.cs.
final class TestIsolationTests: XCTestCase {

    private let realAppDir = FileManager.default
        .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("LanMessenger").path
    private let temp = FileManager.default.temporaryDirectory.path

    func testTheSuiteIsRecognisedAsATestRun() {
        XCTAssertTrue(TestIsolation.isActive)
    }

    /// Only the logger's own tests set the override; everything else — every
    /// MessagingService and remote-desktop test — logs through the default.
    func testWithNoOverrideTheLoggerWritesToScratch() throws {
        let saved = NetLogger._testLogDirectoryOverride
        NetLogger._testLogDirectoryOverride = nil
        defer { NetLogger._testLogDirectoryOverride = saved }

        let dir = NetLogger.logsDirectory.path
        XCTAssertTrue(dir.hasPrefix(temp), dir)
        XCTAssertFalse(dir.hasPrefix(realAppDir), dir)
        for channel in NetLogger.LogChannel.allCases {
            XCTAssertFalse(NetLogger.logURL(for: channel).path.hasPrefix(realAppDir),
                           "\(channel) resolves into the real log directory")
        }

        let marker = "isolation-\(UUID().uuidString)"
        NetLogger.info("TestIsolation", marker)
        NetLogger._testFlush()
        let written = try String(contentsOf: TestIsolation.scratchURL("logs")
            .appendingPathComponent("client.log"), encoding: .utf8)
        XCTAssertTrue(written.contains(marker))
    }

    func testConfigAndTheFilesFiledUnderItLiveInScratch() {
        let store = ConfigStore.shared
        let paths = [store.historyFileURL, store.inboxDirectory,
                     store.updateStagingDirectory, store.logsDirectory].map(\.path)
        for path in paths {
            XCTAssertTrue(path.hasPrefix(temp), path)
            XCTAssertFalse(path.hasPrefix(realAppDir), path)
        }
    }
}
