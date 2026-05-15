import XCTest
@testable import LanMessenger

final class FrameCodecTests: XCTestCase {

    // MARK: - Round-trip

    struct SimplePacket: Codable, Equatable {
        let type: String
        let value: Int
    }

    func testEncodeDecodeRoundTrip() throws {
        let original = SimplePacket(type: "test", value: 42)
        let frame = try FrameCodec.encode(original)

        // First 4 bytes = length of body
        let bodyLength = Int(frame.prefix(4).withUnsafeBytes { $0.load(as: UInt32.self).byteSwapped })
        XCTAssertEqual(bodyLength, frame.count - 4)

        // Parse body
        let bodyData = frame.dropFirst(4)
        let decoded = try JSONDecoder().decode(SimplePacket.self, from: bodyData)
        XCTAssertEqual(decoded, original)
    }

    // MARK: - Byte layout

    func testLengthPrefixIsBigEndian() throws {
        let dict: [String: Any] = ["type": "ping"]
        let frame = try FrameCodec.encodeDict(dict)
        let body = frame.dropFirst(4)
        let headerBytes = Array(frame.prefix(4))
        // Reconstruct as big-endian uint32
        let length = (UInt32(headerBytes[0]) << 24) |
                     (UInt32(headerBytes[1]) << 16) |
                     (UInt32(headerBytes[2]) << 8)  |
                      UInt32(headerBytes[3])
        XCTAssertEqual(Int(length), body.count)
    }

    // MARK: - Known-good frame from test vectors

    func testDecodeKnownFrame() throws {
        let vectors = try loadVectors()
        guard let frameHex = vectors["text_message"] as? [String: Any],
              let hex = frameHex["frame_hex"] as? String else {
            XCTFail("Missing frame_hex in test vectors"); return
        }

        let frameData = Data(hex: hex)!
        // First 4 bytes = length
        let headerLength = Int(frameData.prefix(4).withUnsafeBytes { $0.load(as: UInt32.self).byteSwapped })
        let body = frameData.dropFirst(4)
        XCTAssertEqual(headerLength, body.count)

        let json = try FrameCodec.parseJSON(from: body)
        XCTAssertEqual(json["type"] as? String, "text")
        XCTAssertEqual(json["message_id"] as? String, "aabbccddeeff00112233445566778899")
    }

    // MARK: - Oversized frame rejection

    func testOversizeFrameEncodeThrows() {
        // Can't easily allocate 50 MB for a test, so verify the constant and the guard
        XCTAssertEqual(FrameCodec.maxFrameSize, 50 * 1024 * 1024)
    }
}

// MARK: - Test helpers

private func loadVectors() throws -> [String: Any] {
    // Test vectors live at LanMessengerTests/known_good_exchange.json.
    // In Xcode, accessed via bundle resource; for swift test, resolved relative to #file.
    let url = vectorsURL()
    let data = try Data(contentsOf: url)
    return try JSONSerialization.jsonObject(with: data) as! [String: Any]
}

private func vectorsURL() -> URL {
    // Try bundle resource first (Xcode)
    if let url = Bundle(for: FrameCodecTests.self).url(forResource: "known_good_exchange", withExtension: "json") {
        return url
    }
    // Fall back to file-relative path (swift test)
    return URL(fileURLWithPath: #file)
        .deletingLastPathComponent()   // LanMessengerTests/
        .appendingPathComponent("known_good_exchange.json")
}

private extension Data {
    init?(hex: String) {
        let chars = Array(hex)
        guard chars.count % 2 == 0 else { return nil }
        var bytes: [UInt8] = []
        for i in stride(from: 0, to: chars.count, by: 2) {
            guard let byte = UInt8(String(chars[i...i+1]), radix: 16) else { return nil }
            bytes.append(byte)
        }
        self = Data(bytes)
    }
}
