import XCTest
@testable import LanMessenger

// Tests for the structured logger.
//
// These run against a per-test temp directory; they never touch the user's
// real Application Support folder.  Rotation behaviour is forced by shrinking
// `maxBytes` so we don't have to actually log megabytes of garbage.
final class NetLoggerTests: XCTestCase {

    private var tempDir: URL!
    private var originalMaxBytes: Int!
    private var originalMaxArchives: Int!

    override func setUp() {
        super.setUp()
        tempDir = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("NetLoggerTests-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        NetLogger._testLogDirectoryOverride = tempDir
        NetLogger._testResetHeaderFlag()
        originalMaxBytes = NetLogger.maxBytes
        originalMaxArchives = NetLogger.maxArchives
    }

    override func tearDown() {
        NetLogger._testFlush()
        NetLogger.maxBytes = originalMaxBytes
        NetLogger.maxArchives = originalMaxArchives
        NetLogger._testLogDirectoryOverride = nil
        NetLogger._testResetHeaderFlag()
        // Tests may flip the shared verboseLogging flag; restore it so other
        // suites that read it later see the default state.
        ConfigStore.shared.config.verboseLogging = false
        try? FileManager.default.removeItem(at: tempDir)
        super.tearDown()
    }

    // MARK: - Basic write & format

    func testWriteCreatesFileWithSessionHeader() throws {
        NetLogger.info("Test", "first line")
        NetLogger._testFlush()

        let body = try String(contentsOf: NetLogger.logURL, encoding: .utf8)
        XCTAssertTrue(body.hasPrefix("# Session "),
                      "first line should be a session header, got: \(body.prefix(120))")
        XCTAssertTrue(body.contains("os="),  "session header should include os=")
        XCTAssertTrue(body.contains("arch="), "session header should include arch=")
        XCTAssertTrue(body.contains("host="), "session header should include host=")
        XCTAssertTrue(body.contains("Test: first line"),
                      "actual log line should follow the header")
    }

    func testTimestampHasMillisecondPrecision() throws {
        NetLogger.info("TS", "x")
        NetLogger._testFlush()
        let body = try String(contentsOf: NetLogger.logURL, encoding: .utf8)
        // [yyyy-MM-dd HH:mm:ss.SSSZ] — match the dot-separated ms in the bracket prefix.
        let regex = try NSRegularExpression(
            pattern: #"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z\]"#)
        let range = NSRange(body.startIndex..., in: body)
        XCTAssertGreaterThan(regex.numberOfMatches(in: body, range: range), 0,
                             "expected at least one ms-precision timestamp")
    }

    func testLevelsAreFixedWidth() throws {
        NetLogger.info("L", "a")
        NetLogger.warn("L", "b")
        NetLogger.error("L", "c")
        NetLogger.critical("L", "d")
        NetLogger._testFlush()
        let body = try String(contentsOf: NetLogger.logURL, encoding: .utf8)
        // Each level is padded to 5 chars in the line — verify that the
        // expected literal substrings appear with their padding.
        for level in ["INFO ", "WARN ", "ERROR", "CRIT "] {
            XCTAssertTrue(body.contains("] \(level) L:"),
                          "level token \(level.debugDescription) missing")
        }
    }

    // MARK: - Verbose gating

    func testDebugIsGatedByVerboseFlag() throws {
        ConfigStore.shared.config.verboseLogging = false
        NetLogger.debug("Verbose", "should NOT appear")
        NetLogger._testFlush()
        XCTAssertFalse(FileManager.default.fileExists(atPath: NetLogger.logURL.path),
                       "no log file should be created when verbose is off and only debug is written")

        ConfigStore.shared.config.verboseLogging = true
        NetLogger.debug("Verbose", "should appear")
        NetLogger._testFlush()
        let body = try String(contentsOf: NetLogger.logURL, encoding: .utf8)
        XCTAssertTrue(body.contains("Verbose: should appear"))
        ConfigStore.shared.config.verboseLogging = false
    }

    // MARK: - Structured events

    func testFileTransferEventEmitsKeyValuePairs() throws {
        NetLogger.fileTransfer(
            event: "start", transferId: "abc123", peer: "10.0.0.5",
            direction: "outgoing", filename: "report.pdf",
            size: 1024, mime: "application/pdf"
        )
        NetLogger._testFlush()
        let body = try String(contentsOf: NetLogger.logURL, encoding: .utf8)
        XCTAssertTrue(body.contains("event=start"))
        XCTAssertTrue(body.contains("transfer_id=abc123"))
        XCTAssertTrue(body.contains("peer=10.0.0.5"))
        XCTAssertTrue(body.contains("dir=outgoing"))
        XCTAssertTrue(body.contains("file=report.pdf"))
        XCTAssertTrue(body.contains("size=1024"))
        XCTAssertTrue(body.contains("mime=application/pdf"))
    }

    func testFileTransferFailedIsErrorLevel() throws {
        NetLogger.fileTransfer(event: "failed", transferId: "id", reason: "disk full")
        NetLogger._testFlush()
        let body = try String(contentsOf: NetLogger.logURL, encoding: .utf8)
        XCTAssertTrue(body.contains("ERROR FileTransfer:"),
                      "failed events should log at ERROR level — body was: \(body)")
        XCTAssertTrue(body.contains("reason=\"disk full\""),
                      "reason values with spaces must be quoted")
    }

    func testScreenshotEventCarriesResolutionAndPermission() throws {
        NetLogger.screenshot(
            event: "captured", display: "primary",
            widthPx: 2880, heightPx: 1864,
            permission: "granted", initMs: 42, path: "/tmp/shot.png"
        )
        NetLogger._testFlush()
        let body = try String(contentsOf: NetLogger.logURL, encoding: .utf8)
        XCTAssertTrue(body.contains("res=2880x1864"))
        XCTAssertTrue(body.contains("perm=granted"))
        XCTAssertTrue(body.contains("init_ms=42"))
    }

    func testPeerEventShortensPublicKey() throws {
        let fullKey = "AAAAAAAA-BBBBBBBB-CCCCCCCC-DDDDDDDD-EEEEEEEE-FFFFFFFF"
        NetLogger.peer(event: "connect", peer: "10.0.0.7", publicKey: fullKey)
        NetLogger._testFlush()
        let body = try String(contentsOf: NetLogger.logURL, encoding: .utf8)
        XCTAssertTrue(body.contains("pubkey=AAAAAAAA"),
                      "public key should be shortened to first 8 chars")
        XCTAssertFalse(body.contains("FFFFFFFF"),
                       "full key should never appear in logs")
    }

    // MARK: - Rotation & gzip

    func testRotationProducesGzippedArchive() throws {
        NetLogger.maxBytes = 256          // force rotation quickly
        NetLogger.maxArchives = 2

        // Write enough text to cross the threshold a couple of times.
        for i in 0..<60 {
            NetLogger.info("Rot", "line number \(i) padded with extra characters to bulk it up")
        }
        NetLogger._testFlush()

        let archive = tempDir.appendingPathComponent("client.1.log.gz")
        XCTAssertTrue(FileManager.default.fileExists(atPath: archive.path),
                      "expected client.1.log.gz to exist after rotation")

        let gz = try Data(contentsOf: archive)
        // Verify gzip magic bytes (RFC 1952): 1F 8B.
        XCTAssertGreaterThanOrEqual(gz.count, 18, "archive should be at least header+trailer")
        XCTAssertEqual(gz[0], 0x1F)
        XCTAssertEqual(gz[1], 0x8B)
        XCTAssertEqual(gz[2], 0x08, "deflate method byte should be 0x08")

        // Decompress with system gunzip via Process to prove the file is valid.
        let extracted = tempDir.appendingPathComponent("extracted.log")
        let copy = tempDir.appendingPathComponent("client.1.log.gz.copy")
        try FileManager.default.copyItem(at: archive, to: copy)

        let task = Process()
        task.launchPath = "/usr/bin/gunzip"
        task.arguments = ["-c", copy.path]
        let pipe = Pipe()
        task.standardOutput = pipe
        task.standardError = Pipe()
        try task.run()
        task.waitUntilExit()
        XCTAssertEqual(task.terminationStatus, 0, "gunzip should accept the archive")

        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        try data.write(to: extracted)
        let body = String(decoding: data, as: UTF8.self)
        XCTAssertTrue(body.contains("line number"),
                      "decompressed archive should contain original log lines")
    }

    func testArchivedLogURLsListsNewestFirst() throws {
        NetLogger.maxBytes = 256
        NetLogger.maxArchives = 3
        for i in 0..<200 {
            NetLogger.info("List", "padding padding padding line \(i)")
        }
        NetLogger._testFlush()
        let urls = NetLogger.archivedLogURLs()
        XCTAssertFalse(urls.isEmpty, "should have at least the active log")
        XCTAssertEqual(urls.first?.lastPathComponent, "client.log",
                       "newest file should be the active log")
    }

    // MARK: - CRC32

    func testCRC32MatchesRFC1952Vectors() {
        // From RFC 1952 / standard test vectors.
        XCTAssertEqual(NetLogger.crc32(of: Data()), 0)
        XCTAssertEqual(NetLogger.crc32(of: Data("a".utf8)), 0xE8B7BE43)
        XCTAssertEqual(NetLogger.crc32(of: Data("123456789".utf8)), 0xCBF43926)
    }

    // MARK: - Never crash

    func testGzipOfRandomDataReturnsValidArchive() throws {
        var bytes = [UInt8](repeating: 0, count: 64 * 1024)
        for i in 0..<bytes.count { bytes[i] = UInt8.random(in: 0...255) }
        let input = Data(bytes)
        let gz = NetLogger.gzip(input)
        XCTAssertNotNil(gz)
        XCTAssertEqual(gz![0], 0x1F)
        XCTAssertEqual(gz![1], 0x8B)
    }
}
