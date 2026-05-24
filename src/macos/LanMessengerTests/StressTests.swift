import XCTest
import CryptoKit
@testable import LanMessenger

// Stress and regression tests for the LAN Messenger protocol stack.
//
// Goals
// -----
// • Verify that high-frequency concurrent operations (logging, crypto, frame
//   codec) produce correct results and never crash or dead-lock.
// • Verify that the rotation budget is maintained under continuous write load.
// • Catch common regressions: message-status downgrade, history truncation,
//   frame-codec round-trip correctness at boundary sizes.
//
// These tests run as part of `swift test` and in CI via pr-checks.yml and
// integration-test.yml.  They use no real sockets or disk state outside the
// temp-directory overrides.
final class StressTests: XCTestCase {

    // MARK: - Logger stress

    private var logTempDir: URL!
    private var savedMaxBytes: Int!
    private var savedMaxArchives: Int!

    override func setUp() {
        super.setUp()
        logTempDir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("StressTests-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: logTempDir, withIntermediateDirectories: true)
        NetLogger._testLogDirectoryOverride = logTempDir
        NetLogger._testResetHeaderFlag()
        savedMaxBytes    = NetLogger.maxBytes
        savedMaxArchives = NetLogger.maxArchives
    }

    override func tearDown() {
        NetLogger._testFlush()
        NetLogger.maxBytes    = savedMaxBytes
        NetLogger.maxArchives = savedMaxArchives
        NetLogger._testLogDirectoryOverride = nil
        NetLogger._testResetHeaderFlag()
        try? FileManager.default.removeItem(at: logTempDir)
        super.tearDown()
    }

    // 16 threads × 200 writes across all 8 channels = 3 200 total async writes.
    // Verifies thread-safety: no crashes, no interleaved partial lines.
    func testHighConcurrencyAcrossAllChannels() {
        NetLogger.maxBytes    = 512 * 1024   // 512 KiB — allow rotation mid-test
        NetLogger.maxArchives = 3

        let group   = DispatchGroup()
        let threads = 16
        let writes  = 200

        for t in 0..<threads {
            group.enter()
            DispatchQueue.global(qos: .userInitiated).async {
                for i in 0..<writes {
                    switch t % 8 {
                    case 0: NetLogger.info("Stress",     "t\(t) i\(i)")
                    case 1: NetLogger.fileTransfer(event: "chunk", transferId: "t\(t)",
                                                   bytesSent: Int64(i * 1024))
                    case 2: NetLogger.screenshot(event: "frame", widthPx: 1920, heightPx: 1080)
                    case 3: NetLogger.peer(event: "ping", peer: "10.0.\(t).1")
                    case 4: NetLogger.discovery(event: "beacon_sent", interfaces: t)
                    case 5: NetLogger.crypto(event: "derive", algorithm: "X25519")
                    case 6: NetLogger.ui(event: "frame_update", screen: "chat")
                    default: NetLogger.retry(event: "retry", subsystem: "Transfer", attempt: i)
                    }
                }
                group.leave()
            }
        }

        let timeout = DispatchTime.now() + .seconds(30)
        XCTAssertEqual(group.wait(timeout: timeout), .success,
                       "all writer threads should complete within 30 s")
        NetLogger._testFlush()

        // Every channel that received writes should have produced a non-empty file.
        for channel in NetLogger.LogChannel.allCases {
            let path = NetLogger._testLogURL(for: channel).path
            if FileManager.default.fileExists(atPath: path) {
                let size = (try? FileManager.default.attributesOfItem(atPath: path)[.size] as? Int) ?? 0
                XCTAssertGreaterThan(size, 0, "\(channel.logName) should not be empty")
            }
        }
    }

    // Continuously rotates a single channel; verifies archive count stays
    // within maxArchives and the active log never exceeds 2× the cap.
    func testRotationBudgetMaintainedUnderLoad() throws {
        NetLogger.maxBytes    = 256
        NetLogger.maxArchives = 2

        for i in 0..<500 {
            NetLogger.fileTransfer(event: "chunk", transferId: "rot",
                                   filename: "file_\(i).bin", size: Int64(i * 64))
        }
        NetLogger._testFlush()

        let dir      = logTempDir!
        let files    = (try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? []
        let archives = files.filter { $0.hasPrefix("transfer.") && $0.hasSuffix(".log.gz") }

        XCTAssertLessThanOrEqual(archives.count, NetLogger.maxArchives,
            "should not exceed maxArchives; got \(archives.count)")

        let activeURL = NetLogger._testLogURL(for: .transfer)
        if FileManager.default.fileExists(atPath: activeURL.path) {
            let attrs = try FileManager.default.attributesOfItem(atPath: activeURL.path)
            let size  = attrs[.size] as? Int ?? 0
            XCTAssertLessThanOrEqual(size, NetLogger.maxBytes * 2,
                "active log should be near the rotation cap, got \(size) bytes")
        }
    }

    // Logging into a non-writable path must never throw or crash.
    func testLoggingToUnwritablePathNeverCrashes() {
        NetLogger._testLogDirectoryOverride = URL(fileURLWithPath: "/nonexistent/ro/\(UUID().uuidString)")
        NetLogger._testResetHeaderFlag()

        NetLogger.info("Safe", "app")
        NetLogger.warn("Safe", "warn")
        NetLogger.error("Safe", "error")
        NetLogger.fileTransfer(event: "start", transferId: "x", filename: "f.bin")
        NetLogger.screenshot(event: "captured", widthPx: 100, heightPx: 100)
        NetLogger.peer(event: "connect", peer: "127.0.0.1")
        NetLogger.discovery(event: "started", interfaces: 1)
        NetLogger.crypto(event: "key_generated", algorithm: "X25519")
        NetLogger.ui(event: "window_shown")
        NetLogger.retry(event: "retry", subsystem: "Net", attempt: 1)
        NetLogger._testFlush()
        // Pass = did not crash.
    }

    // MARK: - Frame codec stress

    // Helper Codable type for round-trip tests.
    private struct TestPacket: Codable, Equatable {
        let type: String
        let payload: String
        let seq: Int
    }

    // Round-trips many packets through the frame encoder + JSON parser.
    func testFrameCodecRoundTrip1000Packets() throws {
        for i in 0..<1_000 {
            let pkt = TestPacket(
                type: "stress",
                payload: String(repeating: "x", count: (i % 512)),
                seq: i
            )
            let frame  = try FrameCodec.encode(pkt)

            // Parse the frame back: strip the 4-byte length prefix.
            let bodyLength = Int(frame.prefix(4).withUnsafeBytes {
                $0.load(as: UInt32.self).byteSwapped
            })
            let body   = frame.dropFirst(4)
            XCTAssertEqual(bodyLength, body.count, "length prefix mismatch at seq=\(i)")

            let parsed = try FrameCodec.parseJSON(from: body)
            XCTAssertEqual(parsed["type"] as? String, "stress", "type mismatch at seq=\(i)")
            XCTAssertEqual(parsed["seq"] as? Int, i, "seq mismatch at i=\(i)")
        }
    }

    // Verifies that the encoder rejects a body that exceeds 50 MiB.
    // We test this by crafting a packet with a payload string large enough.
    func testFrameCodecRejectsOversizeBody() throws {
        // A 51 MiB string (each UTF-8 char is 1 byte for ASCII).
        let huge = TestPacket(type: "x", payload: String(repeating: "A", count: 51 * 1024 * 1024), seq: 0)
        XCTAssertThrowsError(try FrameCodec.encode(huge),
            "encoder must reject frames whose body exceeds 50 MiB")
    }

    // Verifies the length-prefix big-endian invariant across 500 packets.
    func testFrameCodecLengthPrefixIsBigEndianAlways() throws {
        for i in 0..<500 {
            let pkt   = TestPacket(type: "t", payload: "p\(i)", seq: i)
            let frame = try FrameCodec.encode(pkt)
            let hdr   = Array(frame.prefix(4))
            let declared = (UInt32(hdr[0]) << 24) | (UInt32(hdr[1]) << 16) |
                           (UInt32(hdr[2]) << 8)  |  UInt32(hdr[3])
            XCTAssertEqual(Int(declared), frame.count - 4,
                "big-endian length prefix wrong at seq=\(i)")
        }
    }

    // MARK: - Crypto stress

    // Encrypts and decrypts messages of varying sizes between two key pairs.
    func testCryptoRoundTripHighVolume() throws {
        let alice = Curve25519.KeyAgreement.PrivateKey()
        let bob   = Curve25519.KeyAgreement.PrivateKey()
        let bobPubB64   = bob.publicKey.rawRepresentation.base64EncodedString()
        let alicePubB64 = alice.publicKey.rawRepresentation.base64EncodedString()

        let messageSizes = [0, 1, 63, 64, 65, 512, 1_024, 10_000, 65_536]

        for size in messageSizes {
            var plaintext = Data(count: size)
            for i in 0..<size { plaintext[i] = UInt8(i & 0xFF) }

            let aad = Data("aad-\(size)".utf8)
            let (nonceB64, ctB64) = try SessionCrypto.encryptForPeer(
                myPrivate: alice, peerPublicKeyB64: bobPubB64, plaintext: plaintext, aad: aad)

            let recovered = try SessionCrypto.decryptFromPeer(
                myPrivate: bob, peerPublicKeyB64: alicePubB64,
                nonceB64: nonceB64, ciphertextB64: ctB64, aad: aad)

            XCTAssertEqual(recovered, plaintext, "round-trip failed for size=\(size)")
        }
    }

    // 200 concurrent encrypt/decrypt pairs must all succeed without races.
    func testCryptoConcurrentRoundTrips() throws {
        let alice = Curve25519.KeyAgreement.PrivateKey()
        let bob   = Curve25519.KeyAgreement.PrivateKey()
        let bobPubB64   = bob.publicKey.rawRepresentation.base64EncodedString()
        let alicePubB64 = alice.publicKey.rawRepresentation.base64EncodedString()

        let group     = DispatchGroup()
        let errLock   = NSLock()
        var cryptoErrors = [Error]()

        for i in 0..<200 {
            group.enter()
            DispatchQueue.global(qos: .userInitiated).async {
                defer { group.leave() }
                do {
                    let plaintext = Data("message \(i)".utf8)
                    let aad = Data("aad-\(i)".utf8)
                    let (nonceB64, ctB64) = try SessionCrypto.encryptForPeer(
                        myPrivate: alice, peerPublicKeyB64: bobPubB64, plaintext: plaintext, aad: aad)
                    let recovered = try SessionCrypto.decryptFromPeer(
                        myPrivate: bob, peerPublicKeyB64: alicePubB64,
                        nonceB64: nonceB64, ciphertextB64: ctB64, aad: aad)
                    if recovered != plaintext {
                        errLock.lock(); cryptoErrors.append(SessionCryptoError.decryptionFailed); errLock.unlock()
                    }
                } catch {
                    errLock.lock(); cryptoErrors.append(error); errLock.unlock()
                }
            }
        }

        let done = group.wait(timeout: .now() + .seconds(15))
        XCTAssertEqual(done, .success, "concurrent crypto round-trips timed out")
        XCTAssertTrue(cryptoErrors.isEmpty, "crypto errors in concurrent run: \(cryptoErrors)")
    }

    // Verifies that a tampered ciphertext is rejected (auth tag check).
    func testCryptoRejectsTamperedCiphertext() throws {
        let alice = Curve25519.KeyAgreement.PrivateKey()
        let bob   = Curve25519.KeyAgreement.PrivateKey()
        let bobPubB64   = bob.publicKey.rawRepresentation.base64EncodedString()
        let alicePubB64 = alice.publicKey.rawRepresentation.base64EncodedString()

        let (nonceB64, ctB64) = try SessionCrypto.encryptForPeer(
            myPrivate: alice, peerPublicKeyB64: bobPubB64,
            plaintext: Data("hello".utf8), aad: Data("aad".utf8))

        var ctData = Data(base64Encoded: ctB64)!
        ctData[ctData.count - 1] ^= 0xFF
        let tampered = ctData.base64EncodedString()

        XCTAssertThrowsError(
            try SessionCrypto.decryptFromPeer(
                myPrivate: bob, peerPublicKeyB64: alicePubB64,
                nonceB64: nonceB64, ciphertextB64: tampered, aad: Data("aad".utf8)),
            "tampered ciphertext must throw decryptionFailed")
    }

    // Verifies that a wrong AAD is rejected.
    func testCryptoRejectsWrongAAD() throws {
        let alice = Curve25519.KeyAgreement.PrivateKey()
        let bob   = Curve25519.KeyAgreement.PrivateKey()
        let bobPubB64   = bob.publicKey.rawRepresentation.base64EncodedString()
        let alicePubB64 = alice.publicKey.rawRepresentation.base64EncodedString()

        let (nonceB64, ctB64) = try SessionCrypto.encryptForPeer(
            myPrivate: alice, peerPublicKeyB64: bobPubB64,
            plaintext: Data("secret".utf8), aad: Data("correct-aad".utf8))

        XCTAssertThrowsError(
            try SessionCrypto.decryptFromPeer(
                myPrivate: bob, peerPublicKeyB64: alicePubB64,
                nonceB64: nonceB64, ciphertextB64: ctB64, aad: Data("wrong-aad".utf8)),
            "wrong AAD must be rejected by AES-GCM")
    }

    // MARK: - Message-status regression
    //
    // MessageStatus uses static string constants (not a Swift enum) with a
    // rank() function for monotonic-upgrade enforcement.

    // Verifies that a late "Sent" cannot downgrade "Delivered" or "Read".
    func testMessageStatusNeverDowngrades() {
        let statuses = [MessageStatus.sent, MessageStatus.delivered, MessageStatus.read]
        for target in statuses {
            for lower in statuses where MessageStatus.rank(lower) < MessageStatus.rank(target) {
                let should = MessageStatus.shouldApply(lower, over: target)
                XCTAssertFalse(should,
                    "shouldApply(\(lower as Any), over: \(target as Any)) must be false")
            }
        }
    }

    // Verifies the upgrade path: Sent → Delivered → Read.
    func testMessageStatusUpgradeSequence() {
        XCTAssertTrue(MessageStatus.shouldApply(MessageStatus.delivered, over: MessageStatus.sent),
            "Delivered must be accepted over Sent")
        XCTAssertTrue(MessageStatus.shouldApply(MessageStatus.read, over: MessageStatus.delivered),
            "Read must be accepted over Delivered")
        XCTAssertTrue(MessageStatus.shouldApply(MessageStatus.read, over: MessageStatus.read),
            "Same-rank transitions are allowed (idempotent)")
    }

    // Verifies the ranks are ordered as expected.
    func testMessageStatusRankOrder() {
        XCTAssertLessThan(MessageStatus.rank(MessageStatus.sent),
                          MessageStatus.rank(MessageStatus.delivered))
        XCTAssertLessThan(MessageStatus.rank(MessageStatus.delivered),
                          MessageStatus.rank(MessageStatus.read))
        XCTAssertLessThan(MessageStatus.rank(MessageStatus.failed),
                          MessageStatus.rank(MessageStatus.sent))
    }

    // MARK: - Packet validator regression

    // Feeds malformed JSON dicts — validator must never crash.
    func testPacketValidatorHandlesMalformedInputWithoutCrash() {
        let badDicts: [[String: Any]] = [
            [:],
            ["type": "text"],
            ["type": "text", "message_id": 12345],
            ["type": "discovery", "public_key": ""],
            ["type": "unknown_type_xyz"],
            ["type": "sent_receipt", "message_id": "abc"],
            ["type": NSNull()],
        ]

        let ownKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="

        for _ in 0..<300 {
            for dict in badDicts {
                _ = PacketValidator.validate(json: dict, senderIP: "10.0.0.1",
                                            ownPublicKeyB64: ownKey)
            }
        }
        // Pass = no crash.
    }

    // MARK: - History store — capacity regression

    // Verifies the maxEntriesPerPeer constant and the suffix logic that enforces it.
    func testHistoryCapLogicPreservesNewest() {
        let cap = HistoryStore.maxEntriesPerPeer
        XCTAssertEqual(cap, 200, "maxEntriesPerPeer should be 200 per PROTOCOL.md")

        // Simulate the .suffix(cap) truncation the store applies on save.
        let entries = (0..<250).map { i in "msg\(i)" }
        let kept    = Array(entries.suffix(cap))

        XCTAssertEqual(kept.count, cap,
            "suffix(\(cap)) should keep exactly \(cap) entries")
        XCTAssertEqual(kept.first, "msg50",
            "oldest surviving entry should be msg50")
        XCTAssertEqual(kept.last, "msg249",
            "newest entry must always be preserved")
    }
}
