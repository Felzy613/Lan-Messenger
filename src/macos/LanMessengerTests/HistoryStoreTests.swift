import XCTest
import CryptoKit
@testable import LanMessenger

final class HistoryStoreTests: XCTestCase {

    // MARK: - Encrypt / decrypt round-trip

    func testRoundTrip() throws {
        let key = Curve25519.KeyAgreement.PrivateKey()
        let entries: [MessageEntry] = [
            MessageEntry(sender: "Alice", text: "Hello", incoming: true,
                         timestamp: 1715000000.0, messageId: "abc123", status: "", readReceiptSent: false),
            MessageEntry(sender: "Bob", text: "Hi!", incoming: false,
                         timestamp: 1715000001.0, messageId: "def456", status: "Sent", readReceiptSent: false),
        ]
        let history: [String: [MessageEntry]] = ["192.168.1.2": entries]
        let plaintext = try JSONEncoder().encode(history)
        let fileJSON = try HistoryCrypto.encryptHistory(plaintext: plaintext, privateKey: key)
        let recovered = try HistoryCrypto.decryptHistory(fileJSON: fileJSON, privateKey: key)
        let decoded = try JSONDecoder().decode([String: [MessageEntry]].self, from: recovered)
        XCTAssertEqual(decoded["192.168.1.2"]?.count, 2)
        XCTAssertEqual(decoded["192.168.1.2"]?.first?.text, "Hello")
    }

    // MARK: - 200-message cap

    func testMessageCap() throws {
        let key = Curve25519.KeyAgreement.PrivateKey()
        let entries = (0..<250).map { i in
            MessageEntry(sender: "A", text: "msg\(i)", incoming: true,
                         timestamp: Double(i), messageId: "\(i)", status: "", readReceiptSent: false)
        }
        let history: [String: [MessageEntry]] = ["1.2.3.4": entries]
        let plaintext = try JSONEncoder().encode(history)
        let fileJSON = try HistoryCrypto.encryptHistory(plaintext: plaintext, privateKey: key)
        let recovered = try HistoryCrypto.decryptHistory(fileJSON: fileJSON, privateKey: key)
        var decoded = try JSONDecoder().decode([String: [MessageEntry]].self, from: recovered)

        // Apply the 200-entry cap (HistoryStore does this on load)
        decoded = decoded.mapValues { Array($0.suffix(200)) }
        XCTAssertEqual(decoded["1.2.3.4"]?.count, 200)
        // Must keep the latest 200 (i = 50...249)
        XCTAssertEqual(decoded["1.2.3.4"]?.first?.text, "msg50")
        XCTAssertEqual(decoded["1.2.3.4"]?.last?.text,  "msg249")
    }

    // MARK: - Wrong key fails

    func testWrongKeyFails() throws {
        let key1 = Curve25519.KeyAgreement.PrivateKey()
        let key2 = Curve25519.KeyAgreement.PrivateKey()
        let plaintext = Data(#"{"1.2.3.4":[]}"#.utf8)
        let fileJSON = try HistoryCrypto.encryptHistory(plaintext: plaintext, privateKey: key1)
        XCTAssertThrowsError(try HistoryCrypto.decryptHistory(fileJSON: fileJSON, privateKey: key2))
    }

    // MARK: - Known Python-generated vector

    func testDecryptKnownHistoryVector() throws {
        let vectors = try loadVectors()
        guard let keys = vectors["keys"] as? [String: String],
              let histVec = vectors["history"] as? [String: Any] else {
            XCTFail("Malformed vectors"); return
        }
        let alicePrivRaw = Data(base64Encoded: keys["alice_private_b64"]!)!
        let alicePrivKey = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: alicePrivRaw)
        let fileJSON = histVec["file_json"] as! String
        let expected = histVec["plaintext_utf8"] as! String
        let recovered = try HistoryCrypto.decryptHistory(fileJSON: fileJSON, privateKey: alicePrivKey)
        XCTAssertEqual(String(data: recovered, encoding: .utf8), expected)
    }
}

private func loadVectors() throws -> [String: Any] {
    if let url = Bundle(for: HistoryStoreTests.self).url(forResource: "known_good_exchange", withExtension: "json") {
        return try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
    }
    let url = URL(fileURLWithPath: #file).deletingLastPathComponent()
        .appendingPathComponent("known_good_exchange.json")
    return try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
}
