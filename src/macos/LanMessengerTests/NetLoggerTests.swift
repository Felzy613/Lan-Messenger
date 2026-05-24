import XCTest
@testable import LanMessenger

// Tests for the structured multi-subsystem logger.
//
// Each test operates on a per-test temp directory; the user's real Application
// Support folder is never touched.  Rotation behaviour is forced by shrinking
// `maxBytes` so we don't have to actually log megabytes of garbage.
//
// Channel routing: generic info/warn/error go to client.log (.app channel).
// Structured helpers go to their dedicated channel file:
//   fileTransfer → transfer.log
//   screenshot   → screenshot.log
//   peer         → peer.log
//   discovery    → discovery.log
//   crypto       → crypto.log
//   ui           → ui.log
//   retry        → retry.log
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
        originalMaxBytes    = NetLogger.maxBytes
        originalMaxArchives = NetLogger.maxArchives
    }

    override func tearDown() {
        NetLogger._testFlush()
        NetLogger.maxBytes    = originalMaxBytes
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
        XCTAssertTrue(body.contains("os="),   "session header should include os=")
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
        // Each level is padded to 5 chars in the line.
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

    // MARK: - Structured events (per-channel routing)

    // fileTransfer → transfer.log
    func testFileTransferEventEmitsKeyValuePairs() throws {
        NetLogger.fileTransfer(
            event: "start", transferId: "abc123", peer: "10.0.0.5",
            direction: "outgoing", filename: "report.pdf",
            size: 1024, mime: "application/pdf"
        )
        NetLogger._testFlush()

        let url  = NetLogger._testLogURL(for: .transfer)
        let body = try String(contentsOf: url, encoding: .utf8)
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
        let url  = NetLogger._testLogURL(for: .transfer)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("ERROR FileTransfer:"),
                      "failed events should log at ERROR level — body was: \(body)")
        XCTAssertTrue(body.contains("reason=\"disk full\""),
                      "reason values with spaces must be quoted")
    }

    func testFileTransferDoesNotWriteToAppLog() throws {
        NetLogger.fileTransfer(event: "start", transferId: "t1")
        NetLogger._testFlush()
        // Transfer events must NOT appear in client.log (they belong in transfer.log).
        XCTAssertFalse(FileManager.default.fileExists(atPath: NetLogger.logURL.path),
                       "transfer events must not bleed into client.log")
    }

    // screenshot → screenshot.log
    func testScreenshotEventCarriesResolutionAndPermission() throws {
        NetLogger.screenshot(
            event: "captured", display: "primary",
            widthPx: 2880, heightPx: 1864,
            permission: "granted", initMs: 42, path: "/tmp/shot.png"
        )
        NetLogger._testFlush()

        let url  = NetLogger._testLogURL(for: .screenshot)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("res=2880x1864"))
        XCTAssertTrue(body.contains("perm=granted"))
        XCTAssertTrue(body.contains("init_ms=42"))
    }

    func testScreenshotPermissionDeniedIsWarn() throws {
        NetLogger.screenshot(event: "permission_denied", permission: "denied")
        NetLogger._testFlush()
        let url  = NetLogger._testLogURL(for: .screenshot)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("WARN  Screenshot:"),
                      "permission_denied should be WARN level")
    }

    func testScreenshotFailedIsError() throws {
        NetLogger.screenshot(event: "failed", reason: "stream interrupted")
        NetLogger._testFlush()
        let url  = NetLogger._testLogURL(for: .screenshot)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("ERROR Screenshot:"),
                      "failed should be ERROR level")
    }

    // peer → peer.log
    func testPeerEventShortensPublicKey() throws {
        let fullKey = "AAAAAAAA-BBBBBBBB-CCCCCCCC-DDDDDDDD-EEEEEEEE-FFFFFFFF"
        NetLogger.peer(event: "connect", peer: "10.0.0.7", publicKey: fullKey)
        NetLogger._testFlush()

        let url  = NetLogger._testLogURL(for: .peer)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("pubkey=AAAAAAAA"),
                      "public key should be shortened to first 8 chars")
        XCTAssertFalse(body.contains("FFFFFFFF"),
                       "full key should never appear in logs")
    }

    func testPeerDisconnectIsWarn() throws {
        NetLogger.peer(event: "disconnect", peer: "10.0.0.2", reason: "timeout")
        NetLogger._testFlush()
        let url  = NetLogger._testLogURL(for: .peer)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("WARN  Peer:"), "disconnect should be WARN level")
    }

    // discovery → discovery.log
    func testDiscoveryEventRoutesToDiscoveryLog() throws {
        NetLogger.discovery(event: "peer_found", ip: "192.168.1.10", interfaces: 2)
        NetLogger._testFlush()

        let url  = NetLogger._testLogURL(for: .discovery)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("event=peer_found"))
        XCTAssertTrue(body.contains("ip=192.168.1.10"))
        XCTAssertTrue(body.contains("interfaces=2"))
    }

    func testDiscoveryDoesNotWriteToAppLog() throws {
        NetLogger.discovery(event: "started")
        NetLogger._testFlush()
        XCTAssertFalse(FileManager.default.fileExists(atPath: NetLogger.logURL.path),
                       "discovery events must not bleed into client.log")
    }

    // crypto → crypto.log
    func testCryptoEventRoutesToCryptoLog() throws {
        NetLogger.crypto(event: "session_key_derived", peer: "10.0.0.3",
                         algorithm: "X25519+AES-GCM", durationMs: 1)
        NetLogger._testFlush()

        let url  = NetLogger._testLogURL(for: .crypto)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("event=session_key_derived"))
        XCTAssertTrue(body.contains("alg=X25519+AES-GCM"))
        XCTAssertTrue(body.contains("ms=1"))
    }

    func testCryptoDecryptFailedIsError() throws {
        NetLogger.crypto(event: "decrypt_failed", peer: "10.0.0.9",
                         reason: "auth tag mismatch")
        NetLogger._testFlush()
        let url  = NetLogger._testLogURL(for: .crypto)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("ERROR Crypto:"),
                      "decrypt_failed should be ERROR level")
    }

    // ui → ui.log
    func testUIEventRoutesToUILog() throws {
        NetLogger.ui(event: "conversation_opened", peer: "10.0.0.4")
        NetLogger._testFlush()

        let url  = NetLogger._testLogURL(for: .ui)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("event=conversation_opened"))
        XCTAssertTrue(body.contains("peer=10.0.0.4"))
    }

    // retry → retry.log
    func testRetryEventRoutesToRetryLog() throws {
        NetLogger.retry(event: "retry", subsystem: "FileTransfer",
                        attempt: 2, maxAttempts: 5, peer: "10.0.0.8",
                        reason: "connection reset")
        NetLogger._testFlush()

        let url  = NetLogger._testLogURL(for: .retry)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("event=retry"))
        XCTAssertTrue(body.contains("subsystem=FileTransfer"))
        XCTAssertTrue(body.contains("attempt=2"))
        XCTAssertTrue(body.contains("max=5"))
    }

    func testRetryExhaustedIsError() throws {
        NetLogger.retry(event: "exhausted", subsystem: "Transfer", attempt: 5, maxAttempts: 5)
        NetLogger._testFlush()
        let url  = NetLogger._testLogURL(for: .retry)
        let body = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(body.contains("ERROR Retry:"),
                      "exhausted should be ERROR level")
    }

    // MARK: - Session header per channel

    func testEachChannelGetsItsOwnSessionHeader() throws {
        NetLogger.info("App", "msg")
        NetLogger.fileTransfer(event: "start", transferId: "t1")
        NetLogger.discovery(event: "started")
        NetLogger._testFlush()

        for channel in [NetLogger.LogChannel.app, .transfer, .discovery] {
            let url  = NetLogger._testLogURL(for: channel)
            let body = try String(contentsOf: url, encoding: .utf8)
            XCTAssertTrue(body.hasPrefix("# Session "),
                          "\(channel.rawValue).log should have a session header")
        }
    }

    // MARK: - archivedLogURLs spans all channels

    func testArchivedLogURLsIncludesAllActiveChannels() throws {
        NetLogger.info("App", "msg")
        NetLogger.fileTransfer(event: "start", transferId: "t1")
        NetLogger.screenshot(event: "captured")
        NetLogger.peer(event: "connect")
        NetLogger.discovery(event: "started")
        NetLogger.crypto(event: "key_generated")
        NetLogger.ui(event: "window_shown")
        NetLogger.retry(event: "retry", subsystem: "Transfer", attempt: 1)
        NetLogger._testFlush()

        let urls = NetLogger.archivedLogURLs()
        let names = Set(urls.map { $0.lastPathComponent })

        for channel in NetLogger.LogChannel.allCases {
            XCTAssertTrue(names.contains(channel.logName),
                          "\(channel.logName) should be in archivedLogURLs()")
        }
    }

    func testArchivedLogURLsListsNewestFirst() throws {
        NetLogger.maxBytes    = 256
        NetLogger.maxArchives = 3
        for i in 0..<200 {
            NetLogger.info("List", "padding padding padding line \(i)")
        }
        NetLogger._testFlush()
        let urls = NetLogger.archivedLogURLs()
        XCTAssertFalse(urls.isEmpty, "should have at least the active log")
        // The active client.log is among the returned files.
        XCTAssertTrue(urls.contains(where: { $0.lastPathComponent == "client.log" }),
                      "client.log should be in archivedLogURLs()")
    }

    // MARK: - Rotation & gzip (per-channel)

    func testRotationProducesGzippedArchive() throws {
        NetLogger.maxBytes    = 256
        NetLogger.maxArchives = 2

        // Write enough text to the app channel to trigger rotation.
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
        let copy = tempDir.appendingPathComponent("client.1.log.gz.copy")
        try FileManager.default.copyItem(at: archive, to: copy)

        let task = Process()
        task.launchPath = "/usr/bin/gunzip"
        task.arguments  = ["-c", copy.path]
        let pipe = Pipe()
        task.standardOutput = pipe
        task.standardError  = Pipe()
        try task.run()
        task.waitUntilExit()
        XCTAssertEqual(task.terminationStatus, 0, "gunzip should accept the archive")

        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        let body = String(decoding: data, as: UTF8.self)
        XCTAssertTrue(body.contains("line number"),
                      "decompressed archive should contain original log lines")
    }

    func testSubsystemChannelRotatesIndependently() throws {
        NetLogger.maxBytes    = 200
        NetLogger.maxArchives = 1

        // Force rotation on the transfer channel only.
        for i in 0..<80 {
            NetLogger.fileTransfer(event: "chunk",
                                   transferId: "tid", filename: "bigfile_\(i).bin",
                                   size: 65536)
        }
        NetLogger._testFlush()

        // transfer.1.log.gz should exist; client.1.log.gz should NOT.
        let transferArchive = tempDir.appendingPathComponent("transfer.1.log.gz")
        let clientArchive   = tempDir.appendingPathComponent("client.1.log.gz")
        XCTAssertTrue(FileManager.default.fileExists(atPath: transferArchive.path),
                      "transfer.1.log.gz should exist after transfer-channel rotation")
        XCTAssertFalse(FileManager.default.fileExists(atPath: clientArchive.path),
                       "client.1.log.gz must not be created when only transfer channel rotates")
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

    func testWriteToReadOnlyDirectoryNeverCrashes() {
        // Point the logger at a path that cannot be created.
        NetLogger._testLogDirectoryOverride = URL(fileURLWithPath: "/nonexistent/ro/path")
        NetLogger._testResetHeaderFlag()
        // Must not throw — logging failures are always swallowed.
        NetLogger.info("Resilient", "should be silently dropped")
        NetLogger.fileTransfer(event: "start", transferId: "noop")
        NetLogger._testFlush()
    }

    // MARK: - Stress tests

    func testConcurrentWritesToMultipleChannels() {
        // 8 threads × 50 writes across 4 channels = 400 total writes.
        // All must complete without data races or crashes.
        let group = DispatchGroup()
        for threadIdx in 0..<8 {
            group.enter()
            DispatchQueue.global(qos: .userInitiated).async {
                for i in 0..<50 {
                    switch threadIdx % 4 {
                    case 0: NetLogger.info("Stress", "thread \(threadIdx) line \(i)")
                    case 1: NetLogger.fileTransfer(event: "chunk", transferId: "t\(threadIdx)")
                    case 2: NetLogger.peer(event: "ping", peer: "10.0.\(threadIdx).1")
                    default: NetLogger.discovery(event: "beacon_sent", interfaces: threadIdx)
                    }
                }
                group.leave()
            }
        }
        group.wait()
        NetLogger._testFlush()

        // At minimum the app and transfer channels should have produced files.
        XCTAssertTrue(FileManager.default.fileExists(atPath: NetLogger.logURL.path),
                      "client.log should exist after concurrent writes")
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: NetLogger._testLogURL(for: .transfer).path),
            "transfer.log should exist after concurrent writes")
    }

    func testRotationUnderContinuousLoad() throws {
        NetLogger.maxBytes    = 128
        NetLogger.maxArchives = 2

        // Drive the app channel well past two rotation cycles.
        for i in 0..<300 {
            NetLogger.info("Load", "stress write \(i) aaaaaaaaaaaaaaaaaaaaaa")
        }
        NetLogger._testFlush()

        // Active log must exist and not be absurdly large.
        let attrs = try FileManager.default.attributesOfItem(atPath: NetLogger.logURL.path)
        let size = attrs[.size] as? Int ?? 0
        XCTAssertLessThanOrEqual(size, NetLogger.maxBytes * 2,
                                 "active log should be near the rotation threshold")

        // At most maxArchives gzip files for the client channel.
        let gzFiles = try FileManager.default.contentsOfDirectory(atPath: tempDir.path)
            .filter { $0.hasPrefix("client.") && $0.hasSuffix(".log.gz") }
        XCTAssertLessThanOrEqual(gzFiles.count, NetLogger.maxArchives,
                                 "should not accumulate more than maxArchives gzip archives")
    }

    func testAllChannelsPresentAfterMixedLoad() throws {
        // Write at least one event to every channel.
        NetLogger.info("App", "startup")
        NetLogger.fileTransfer(event: "start", transferId: "t1", filename: "a.txt")
        NetLogger.screenshot(event: "captured", widthPx: 1920, heightPx: 1080)
        NetLogger.peer(event: "connected", peer: "10.0.0.1")
        NetLogger.discovery(event: "peer_found", ip: "10.0.0.2", interfaces: 1)
        NetLogger.crypto(event: "session_key_derived", algorithm: "X25519")
        NetLogger.ui(event: "window_shown", screen: "main")
        NetLogger.retry(event: "retry", subsystem: "Messaging", attempt: 1)
        NetLogger._testFlush()

        // Every channel should have a log file.
        for channel in NetLogger.LogChannel.allCases {
            let path = NetLogger._testLogURL(for: channel).path
            XCTAssertTrue(FileManager.default.fileExists(atPath: path),
                          "\(channel.logName) should exist after one event per channel")
        }
    }
}
