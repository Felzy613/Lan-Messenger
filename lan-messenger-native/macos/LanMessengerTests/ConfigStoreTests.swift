import XCTest
@testable import LanMessenger

// ConfigStore reads/writes to Application Support so these are integration-style tests.
// They operate on a temp directory to avoid polluting real user data.
final class ConfigStoreTests: XCTestCase {

    // MARK: - Filename sanitization (pure logic, no disk I/O)

    func testSanitizeNormal() {
        XCTAssertEqual(PacketValidator.sanitizeFilename("photo.jpg"), "photo.jpg")
    }

    func testSanitizePathTraversal() {
        XCTAssertEqual(PacketValidator.sanitizeFilename("../../etc/passwd"), "passwd")
    }

    func testSanitizeEmpty() {
        XCTAssertEqual(PacketValidator.sanitizeFilename(""), "file")
    }

    func testSanitizeNullByte() {
        // Null bytes stripped; file stays in same directory component
        XCTAssertEqual(PacketValidator.sanitizeFilename("bad\0name.txt"), "badname.txt")
    }

    // MARK: - AppConfig round-trip (JSON encoding, no disk)

    func testConfigEncodeDecode() throws {
        var config = AppConfig()
        config.username = "TestUser"
        config.contacts = [
            ContactConfig(publicKeyB64: "AAAA", username: "Alice", lastIP: "10.0.0.1")
        ]
        config.hiddenConversations = ["10.0.0.2"]

        let data = try JSONEncoder().encode(config)
        let decoded = try JSONDecoder().decode(AppConfig.self, from: data)

        XCTAssertEqual(decoded.username, "TestUser")
        XCTAssertEqual(decoded.contacts.count, 1)
        XCTAssertEqual(decoded.contacts.first?.username, "Alice")
        XCTAssertEqual(decoded.hiddenConversations, ["10.0.0.2"])
    }

    func testConfigCodingKeys() throws {
        // Verify JSON field names match the Python config format
        var config = AppConfig()
        config.username = "Dave"
        config.updateServerURL = "https://example.com/update.json"
        config.inboxDir = "/tmp/received"

        let data = try JSONEncoder().encode(config)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]

        XCTAssertNotNil(json["username"])
        XCTAssertNotNil(json["update_server_url"])   // snake_case matching Python
        XCTAssertNotNil(json["inbox_dir"])
        XCTAssertNotNil(json["hidden_conversations"])
        XCTAssertNotNil(json["pending_messages"])
    }

    // MARK: - PendingMessageConfig round-trip

    func testPendingMessageEncodeDecode() throws {
        let msg = PendingMessageConfig(
            messageId: "abc123",
            peerPublicKeyB64: "AAAA",
            peerUsername: "Bob",
            text: "Hey!",
            timestamp: 1715000000.0
        )
        let data = try JSONEncoder().encode(msg)
        let decoded = try JSONDecoder().decode(PendingMessageConfig.self, from: data)
        XCTAssertEqual(decoded.messageId, "abc123")
        XCTAssertEqual(decoded.text, "Hey!")
    }

    // MARK: - ContactConfig round-trip

    func testContactConfigEncodeDecode() throws {
        let contact = ContactConfig(publicKeyB64: "BBBB", username: "Carol", lastIP: "192.168.1.10")
        let data = try JSONEncoder().encode(contact)
        let decoded = try JSONDecoder().decode(ContactConfig.self, from: data)
        XCTAssertEqual(decoded.username, "Carol")
        XCTAssertEqual(decoded.lastIP, "192.168.1.10")

        // Verify snake_case field names in the JSON output
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        XCTAssertNotNil(json["public_key_b64"])
        XCTAssertNotNil(json["last_ip"])
    }
}
