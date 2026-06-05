import XCTest
@testable import LanMessenger

final class PacketValidatorTests: XCTestCase {

    let ownKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="   // 32 zero bytes b64
    let peerKey = "AQIDBA=="                                        // different key

    // MARK: - Type validation

    func testMissingType() {
        let result = PacketValidator.validate(json: [:], senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .failure(let e) = result, case .missingType = e else { XCTFail(); return }
    }

    func testUnknownType() {
        let json: [String: Any] = ["type": "banana"]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .failure(let e) = result, case .unknownType = e else { XCTFail(); return }
    }

    // MARK: - Self-suppression

    func testSelfPacketDropped() {
        let json: [String: Any] = [
            "type": "typing",
            "active": true,
            "sender": "Me",
            "sender_public_key_b64": ownKey,
            "port": 54232,
        ]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .failure(let e) = result, case .selfPacket = e else { XCTFail(); return }
    }

    // MARK: - Text

    func testValidTextPacket() {
        let nonce = Data(repeating: 0, count: 12).base64EncodedString()
        let ct = Data(repeating: 0, count: 32).base64EncodedString()
        let json: [String: Any] = [
            "type": "text",
            "message_id": "aabbccddeeff00112233445566778899",
            "timestamp": 1715000000.0,
            "sender": "Bob",
            "sender_public_key_b64": peerKey,
            "port": 54232,
            "nonce": nonce,
            "ciphertext": ct,
        ]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .success(let pkt) = result, case .text = pkt else { XCTFail(); return }
    }

    func testTextBadNonce() {
        let badNonce = Data(repeating: 0, count: 8).base64EncodedString()   // not 12 bytes
        let json: [String: Any] = [
            "type": "text",
            "message_id": "aabbccddeeff00112233445566778899",
            "timestamp": 1715000000.0,
            "sender": "Bob",
            "sender_public_key_b64": peerKey,
            "port": 54232,
            "nonce": badNonce,
            "ciphertext": Data(repeating: 0, count: 32).base64EncodedString(),
        ]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .failure(let e) = result, case .invalidNonce = e else { XCTFail(); return }
    }

    // MARK: - Typing

    func testValidTypingPacket() {
        let json: [String: Any] = [
            "type": "typing", "active": true, "sender": "Bob",
            "sender_public_key_b64": peerKey, "port": 54232,
        ]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .success(let pkt) = result, case .typing = pkt else { XCTFail(); return }
    }

    // MARK: - Receipts

    func testValidSentReceipt() {
        let json: [String: Any] = [
            "type": "sent_receipt",
            "message_id": "aabbccddeeff00112233445566778899",
            "sender": "Bob", "sender_public_key_b64": peerKey, "port": 54232,
        ]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .success(let pkt) = result, case .receipt = pkt else { XCTFail(); return }
    }

    func testValidReadReceipt() {
        let json: [String: Any] = [
            "type": "read_receipt",
            "message_id": "aabbccddeeff00112233445566778899",
            "sender": "Bob", "sender_public_key_b64": peerKey, "port": 54232,
        ]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .success(let pkt) = result, case .receipt = pkt else { XCTFail(); return }
    }

    // MARK: - File transfer

    func testValidFileStart() {
        let json: [String: Any] = [
            "type": "file_start",
            "transfer_id": "aabbccddeeff00112233445566778899",
            "filename": "photo.jpg", "size": 1048576,
            "sender": "Bob", "sender_public_key_b64": peerKey, "port": 54232,
        ]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .success = result else { XCTFail(); return }
    }

    func testFileStartNegativeSize() {
        let json: [String: Any] = [
            "type": "file_start",
            "transfer_id": "aabbccddeeff00112233445566778899",
            "filename": "photo.jpg", "size": -1,
            "sender": "Bob", "sender_public_key_b64": peerKey, "port": 54232,
        ]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .failure(let e) = result, case .invalidFileSize = e else { XCTFail(); return }
    }

    func testFileStartTooBig() {
        let json: [String: Any] = [
            "type": "file_start",
            "transfer_id": "aabbccddeeff00112233445566778899",
            "filename": "photo.jpg", "size": Int64(3) * 1024 * 1024 * 1024,
            "sender": "Bob", "sender_public_key_b64": peerKey, "port": 54232,
        ]
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .failure(let e) = result, case .invalidFileSize = e else { XCTFail(); return }
    }

    // MARK: - Filename sanitization

    func testFilenamePathTraversal() {
        XCTAssertEqual(PacketValidator.sanitizeFilename("../../../evil.sh"), "evil.sh")
    }

    func testFilenameWindowsPathSeparator() {
        // On POSIX, backslash is not a path separator (same as Python pathlib on macOS).
        // A Windows peer sending "..\..\evil.exe" is kept as-is; the path traversal
        // protection only applies to forward slashes.
        XCTAssertEqual(PacketValidator.sanitizeFilename("..\\..\\evil.exe"), "..\\..\\evil.exe")
    }

    func testFilenameEmpty() {
        XCTAssertEqual(PacketValidator.sanitizeFilename(""), "file")
    }

    func testFilenameNullByte() {
        // Null bytes are removed (Python: name.replace("\x00", ""))
        XCTAssertEqual(PacketValidator.sanitizeFilename("evil\0.txt"), "evil.txt")
    }

    func testFilenameNormal() {
        XCTAssertEqual(PacketValidator.sanitizeFilename("photo.jpg"), "photo.jpg")
    }

    // MARK: - Discovery validation

    private func discoveryData(type: String, key: String) -> Data {
        let json: [String: Any] = [
            "type": type,
            "username": "Bob",
            "port": 54232,
            "public_key_b64": key,
            "ips": ["1.2.3.4"],
        ]
        return try! JSONSerialization.data(withJSONObject: json)
    }

    func testValidateDiscoveryAcceptsDiscovery() {
        let data = discoveryData(type: "discovery", key: peerKey)
        let pkt = PacketValidator.validateDiscovery(data: data, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey, ownIPs: [])
        XCTAssertEqual(pkt?.type, "discovery")
    }

    func testValidateDiscoveryAcceptsGoodbye() {
        // The departure datagram must pass the validator, otherwise peers can
        // never flip offline promptly. This was the missing piece in the rebuild.
        let data = discoveryData(type: "goodbye", key: peerKey)
        let pkt = PacketValidator.validateDiscovery(data: data, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey, ownIPs: [])
        XCTAssertEqual(pkt?.type, "goodbye")
        XCTAssertEqual(pkt?.publicKeyB64, peerKey)
    }

    func testValidateDiscoveryRejectsUnknownType() {
        let data = discoveryData(type: "banana", key: peerKey)
        XCTAssertNil(PacketValidator.validateDiscovery(data: data, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey, ownIPs: []))
    }

    func testValidateDiscoveryDropsOwnPacket() {
        let data = discoveryData(type: "goodbye", key: ownKey)
        XCTAssertNil(PacketValidator.validateDiscovery(data: data, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey, ownIPs: []))
    }
}
