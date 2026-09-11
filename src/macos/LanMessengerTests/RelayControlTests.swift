import XCTest
@testable import LanMessenger

// Guards the relay control envelope — the path that carries an edit or delete
// to a peer who wasn't on the LAN when it happened.
//
// See PROTOCOL.md → Relay control records. The envelope lives in the *plaintext*
// of an ordinary relay record, so the Worker needs no change and still learns
// nothing: not the operation, not which message it targets.
final class RelayControlTests: XCTestCase {

    // MARK: - Envelope round-trip

    func testEditEnvelopeRoundTrips() throws {
        let env = RelayControlEnvelope(op: .edit, target: String(repeating: "a", count: 32),
                                       text: "corrected text", at: 1715000000.5)
        let decoded = try XCTUnwrap(RelayControlEnvelope.decode(env.encoded()))
        XCTAssertEqual(decoded, env)
    }

    func testDeleteEnvelopeRoundTrips() throws {
        let env = RelayControlEnvelope(op: .delete, target: String(repeating: "b", count: 32),
                                       text: nil, at: 1715000001)
        let decoded = try XCTUnwrap(RelayControlEnvelope.decode(env.encoded()))
        XCTAssertEqual(decoded, env)
    }

    func testEncodedFormStartsWithTheMarker() {
        let env = RelayControlEnvelope(op: .delete, target: String(repeating: "c", count: 32),
                                       text: nil, at: 1)
        XCTAssertTrue(env.encoded().hasPrefix("__CTRL__:"))
    }

    // MARK: - Ordinary chat text must not be mistaken for a control record

    func testPlainTextIsNotAControlEnvelope() {
        XCTAssertNil(RelayControlEnvelope.decode("hey, are you around?"))
        XCTAssertNil(RelayControlEnvelope.decode(""))
        XCTAssertNil(RelayControlEnvelope.decode("{\"op\":\"delete\"}"))
        // A file message is a different prefixed convention entirely.
        XCTAssertNil(RelayControlEnvelope.decode("__FILE__:/tmp/report.pdf"))
    }

    func testMarkerWithGarbagePayloadIsRejected() {
        XCTAssertNil(RelayControlEnvelope.decode("__CTRL__:not json"))
        XCTAssertNil(RelayControlEnvelope.decode("__CTRL__:"))
    }

    func testUnknownOpIsRejected() {
        let json = "__CTRL__:{\"op\":\"wipe\",\"target\":\"\(String(repeating: "a", count: 32))\",\"at\":1}"
        XCTAssertNil(RelayControlEnvelope.decode(json))
    }

    // A target that isn't a message id can't match anything, so it is rejected
    // before it reaches the history store rather than after.
    func testMalformedTargetIsRejected() {
        for bad in ["", "short", String(repeating: "A", count: 32), String(repeating: "a", count: 31)] {
            let json = "__CTRL__:{\"op\":\"delete\",\"target\":\"\(bad)\",\"at\":1}"
            XCTAssertNil(RelayControlEnvelope.decode(json), "should reject target '\(bad)'")
        }
    }

    // An edit with no replacement body would blank the message rather than
    // change it — that is what `delete` is for.
    func testEditWithoutTextIsRejected() {
        let id = String(repeating: "a", count: 32)
        XCTAssertNil(RelayControlEnvelope.decode("__CTRL__:{\"op\":\"edit\",\"target\":\"\(id)\",\"at\":1}"))
        XCTAssertNil(RelayControlEnvelope.decode("__CTRL__:{\"op\":\"edit\",\"target\":\"\(id)\",\"text\":\"\",\"at\":1}"))
    }

    // MARK: - Record id

    // The record must NOT reuse the target's id: the Worker dedups /store on
    // message_id and answers a repeat with {ok:true,duplicate:true}, so a
    // re-post under the original id is dropped while reporting success.
    func testNewRecordIdIsAFreshMessageId() {
        let a = RelayControlEnvelope.newRecordId()
        let b = RelayControlEnvelope.newRecordId()
        XCTAssertNotEqual(a, b)
        XCTAssertTrue(RelayControlEnvelope.isMessageId(a), "record id must be a valid message_id: \(a)")
        XCTAssertEqual(a.count, 32)
    }

    func testIsMessageIdMatchesProtocolForm() {
        XCTAssertTrue(RelayControlEnvelope.isMessageId("a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6"))
        XCTAssertFalse(RelayControlEnvelope.isMessageId("A3F1B2C4D5E6F7A8B9C0D1E2F3A4B5C6"), "must be lowercase")
        XCTAssertFalse(RelayControlEnvelope.isMessageId("a3f1b2c4-d5e6-f7a8-b9c0-d1e2f3a4b5c6"), "no dashes")
        XCTAssertFalse(RelayControlEnvelope.isMessageId("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"), "must be hex")
    }
}

// The delete gate, matching the one applyEdit already has.
final class DeleteGateTests: XCTestCase {

    private let peer = "192.168.99.79"

    override func tearDown() {
        HistoryStore.shared.delete(peerIP: peer)
        super.tearDown()
    }

    func testInboundDeleteRemovesThePeersOwnMessage() {
        HistoryStore.shared.append(
            entry: MessageEntry(sender: "Peer", text: "oops", incoming: true, timestamp: 1,
                                messageId: "del-theirs", status: "", readReceiptSent: false),
            forPeerIP: peer)

        XCTAssertTrue(HistoryStore.shared.markDeleted(messageId: "del-theirs", peerIP: peer,
                                                      requireIncoming: true))
        let e = HistoryStore.shared.entries(forPeerIP: peer).first { $0.messageId == "del-theirs" }
        XCTAssertEqual(e?.deleted, true)
        XCTAssertEqual(e?.text, "")
    }

    // The same spoofing shape as the edit gate: a peer knows the message_id of
    // everything we sent them, so an inbound delete naming one of our own
    // messages must be refused rather than allowed to blank what we said.
    func testInboundDeleteCannotBlankOurOwnMessage() {
        HistoryStore.shared.append(
            entry: MessageEntry(sender: "me", text: "the terms are $500", incoming: false, timestamp: 1,
                                messageId: "del-spoof", status: "Sent", readReceiptSent: false),
            forPeerIP: peer)

        XCTAssertFalse(HistoryStore.shared.markDeleted(messageId: "del-spoof", peerIP: peer,
                                                       requireIncoming: true))
        let e = HistoryStore.shared.entries(forPeerIP: peer).first { $0.messageId == "del-spoof" }
        XCTAssertEqual(e?.text, "the terms are $500")
        XCTAssertEqual(e?.deleted, false)
    }

    func testLocalDeleteForEveryoneMarksOurOwnMessage() {
        HistoryStore.shared.append(
            entry: MessageEntry(sender: "me", text: "never mind", incoming: false, timestamp: 1,
                                messageId: "del-own", status: "Sent", readReceiptSent: false),
            forPeerIP: peer)

        XCTAssertTrue(HistoryStore.shared.markDeleted(messageId: "del-own", peerIP: peer,
                                                      requireIncoming: false))
        XCTAssertEqual(HistoryStore.shared.entries(forPeerIP: peer)
            .first { $0.messageId == "del-own" }?.deleted, true)
    }
}
