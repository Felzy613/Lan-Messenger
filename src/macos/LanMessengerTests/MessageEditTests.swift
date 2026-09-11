import XCTest
@testable import LanMessenger

// Guards message editing. See PROTOCOL.md → edit_message.
//
// The rule worth the most attention is `requireIncoming`. A peer knows the
// message_id of every message we ever sent them — we put it in the packet — so
// an inbound edit_message naming one of OUR outgoing messages would let them
// rewrite what we said in our own transcript. That is the one failure here with
// real consequences, and it is invisible unless something asserts it.
final class MessageEditTests: XCTestCase {

    private let peer = "192.168.99.77"

    override func tearDown() {
        HistoryStore.shared.delete(peerIP: peer)
        super.tearDown()
    }

    private func seed(_ entry: MessageEntry) {
        HistoryStore.shared.append(entry: entry, forPeerIP: peer)
    }

    private func stored(_ messageId: String) -> MessageEntry? {
        HistoryStore.shared.entries(forPeerIP: peer).first { $0.messageId == messageId }
    }

    private func outgoing(_ id: String, _ text: String) -> MessageEntry {
        MessageEntry(sender: "me", text: text, incoming: false,
                     timestamp: 100, messageId: id, status: "Sent", readReceiptSent: false)
    }

    private func incoming(_ id: String, _ text: String) -> MessageEntry {
        MessageEntry(sender: "Peer", text: text, incoming: true,
                     timestamp: 100, messageId: id, status: "", readReceiptSent: false)
    }

    // MARK: - The happy paths

    func testSenderEditsOwnOutgoingMessage() {
        seed(outgoing("edit-own", "teh original"))

        XCTAssertTrue(HistoryStore.shared.applyEdit(
            messageId: "edit-own", peerIP: peer,
            newText: "the original", editedAt: 555, requireIncoming: false))

        let e = stored("edit-own")
        XCTAssertEqual(e?.text, "the original")
        XCTAssertEqual(e?.edited, true)
        XCTAssertEqual(e?.editedAt, 555)
        // The original send time must survive, or the message jumps position in
        // the thread the moment it is edited.
        XCTAssertEqual(e?.timestamp, 100)
    }

    func testInboundEditRewritesThePeersOwnMessage() {
        seed(incoming("edit-theirs", "hlelo"))

        XCTAssertTrue(HistoryStore.shared.applyEdit(
            messageId: "edit-theirs", peerIP: peer,
            newText: "hello", editedAt: 556, requireIncoming: true))

        XCTAssertEqual(stored("edit-theirs")?.text, "hello")
        XCTAssertEqual(stored("edit-theirs")?.edited, true)
    }

    // MARK: - The security gate

    func testInboundEditCannotRewriteOurOwnOutgoingMessage() {
        seed(outgoing("edit-spoof", "I agree to the terms"))

        // Exactly what a malicious peer would send: they know this id, because
        // we sent it to them.
        XCTAssertFalse(HistoryStore.shared.applyEdit(
            messageId: "edit-spoof", peerIP: peer,
            newText: "I agree to pay $10,000", editedAt: 557, requireIncoming: true))

        let e = stored("edit-spoof")
        XCTAssertEqual(e?.text, "I agree to the terms", "an inbound edit must never touch our own message")
        XCTAssertEqual(e?.edited, false)
    }

    func testLocalEditCannotRewriteAMessageWeReceived() {
        seed(incoming("edit-inbound", "what they said"))

        XCTAssertFalse(HistoryStore.shared.applyEdit(
            messageId: "edit-inbound", peerIP: peer,
            newText: "what I wish they said", editedAt: 558, requireIncoming: false))

        XCTAssertEqual(stored("edit-inbound")?.text, "what they said")
    }

    // MARK: - What is not editable

    // An attachment's `text` is a local filesystem path, not a message body.
    // Replacing it would repoint the bubble at a file that may not exist and
    // has nothing to do with what was transferred.
    func testAttachmentsAreNotEditable() {
        seed(outgoing("edit-file", "__FILE__:/Users/dave/Downloads/report.pdf"))

        XCTAssertFalse(HistoryStore.shared.applyEdit(
            messageId: "edit-file", peerIP: peer,
            newText: "something else", editedAt: 559, requireIncoming: false))

        XCTAssertEqual(stored("edit-file")?.text, "__FILE__:/Users/dave/Downloads/report.pdf")
    }

    func testDeletedMessagesAreNotEditable() {
        var e = outgoing("edit-deleted", "")
        e.deleted = true
        seed(e)

        XCTAssertFalse(HistoryStore.shared.applyEdit(
            messageId: "edit-deleted", peerIP: peer,
            newText: "undelete me", editedAt: 560, requireIncoming: false))

        XCTAssertEqual(stored("edit-deleted")?.text, "")
        XCTAssertEqual(stored("edit-deleted")?.deleted, true)
    }

    func testUnknownMessageIdChangesNothing() {
        seed(outgoing("edit-present", "untouched"))

        XCTAssertFalse(HistoryStore.shared.applyEdit(
            messageId: "edit-absent", peerIP: peer,
            newText: "ghost", editedAt: 561, requireIncoming: false))

        XCTAssertEqual(stored("edit-present")?.text, "untouched")
    }

    func testUnknownPeerChangesNothing() {
        XCTAssertFalse(HistoryStore.shared.applyEdit(
            messageId: "anything", peerIP: "10.255.255.254",
            newText: "ghost", editedAt: 562, requireIncoming: true))
    }

    // MARK: - History wire format

    // Older history files predate both fields and must still load.
    func testHistoryEntryDecodesWithoutEditFields() throws {
        let json = """
        {"sender":"Alice","text":"Hello","incoming":true,"timestamp":1715000000.0,
         "message_id":"abc","status":"","read_receipt_sent":false}
        """.data(using: .utf8)!

        let entry = try JSONDecoder().decode(MessageEntry.self, from: json)
        XCTAssertFalse(entry.edited)
        XCTAssertNil(entry.editedAt)
    }

    func testHistoryEntryRoundTripsEditFields() throws {
        let entry = MessageEntry(sender: "me", text: "fixed", incoming: false,
                                 timestamp: 1, messageId: "rt", status: "Sent",
                                 readReceiptSent: false, edited: true, editedAt: 42)
        let decoded = try JSONDecoder().decode(MessageEntry.self, from: JSONEncoder().encode(entry))
        XCTAssertTrue(decoded.edited)
        XCTAssertEqual(decoded.editedAt, 42)
    }

    func testEditFieldsUseSnakeCaseOnTheWire() throws {
        let entry = MessageEntry(sender: "me", text: "x", incoming: false,
                                 timestamp: 1, messageId: "sc", status: "",
                                 readReceiptSent: false, edited: true, editedAt: 7)
        let json = try XCTUnwrap(String(data: JSONEncoder().encode(entry), encoding: .utf8))
        XCTAssertTrue(json.contains("\"edited_at\""), "cross-platform field name must be snake_case: \(json)")
    }

    // MARK: - Packet validation

    private let ownKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
    private let peerKey = "AQIDBA=="

    private func editPacket(messageId: String = "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
                            nonceBytes: Int = 12) -> [String: Any] {
        [
            "type": "edit_message",
            "message_id": messageId,
            "timestamp": 1715000123.456,
            "sender": "Alice",
            "sender_public_key_b64": peerKey,
            "port": 54232,
            "nonce": Data(repeating: 0, count: nonceBytes).base64EncodedString(),
            "ciphertext": Data(repeating: 1, count: 48).base64EncodedString(),
        ]
    }

    func testValidEditPacketParses() {
        let result = PacketValidator.validate(json: editPacket(), senderIP: "1.2.3.4",
                                              ownPublicKeyB64: ownKey)
        guard case .success(.edit(let pkt, let ip)) = result else {
            XCTFail("expected .edit, got \(result)"); return
        }
        XCTAssertEqual(pkt.messageId, "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6")
        XCTAssertEqual(ip, "1.2.3.4")
    }

    // Same 12-byte rule as `text` — the AES-GCM nonce size is fixed.
    func testEditPacketRejectsWrongNonceLength() {
        let result = PacketValidator.validate(json: editPacket(nonceBytes: 16), senderIP: "1.2.3.4",
                                              ownPublicKeyB64: ownKey)
        guard case .failure(.invalidNonce) = result else {
            XCTFail("expected .invalidNonce, got \(result)"); return
        }
    }

    func testEditPacketRejectsEmptyMessageId() {
        let result = PacketValidator.validate(json: editPacket(messageId: ""), senderIP: "1.2.3.4",
                                              ownPublicKeyB64: ownKey)
        guard case .failure(.missingRequiredField) = result else {
            XCTFail("expected .missingRequiredField, got \(result)"); return
        }
    }

    func testEditPacketFromSelfIsDropped() {
        var json = editPacket()
        json["sender_public_key_b64"] = ownKey
        let result = PacketValidator.validate(json: json, senderIP: "1.2.3.4", ownPublicKeyB64: ownKey)
        guard case .failure(.selfPacket) = result else {
            XCTFail("expected .selfPacket, got \(result)"); return
        }
    }
}
