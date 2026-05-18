import XCTest
@testable import LanMessenger

// Guards the race-condition fix that caused the "single check mark" symptom
// in cross-platform Mac↔Windows messaging. See MessageStatus.swift.
final class MessageStatusTests: XCTestCase {

    func testRankOrderIsMonotonic() {
        XCTAssertGreaterThan(MessageStatus.rank(MessageStatus.read),      MessageStatus.rank(MessageStatus.delivered))
        XCTAssertGreaterThan(MessageStatus.rank(MessageStatus.delivered), MessageStatus.rank(MessageStatus.sent))
        XCTAssertGreaterThan(MessageStatus.rank(MessageStatus.sent),      MessageStatus.rank(MessageStatus.queued))
        XCTAssertGreaterThan(MessageStatus.rank(MessageStatus.sent),      MessageStatus.rank(MessageStatus.sending))
        XCTAssertGreaterThan(MessageStatus.rank(MessageStatus.sent),      MessageStatus.rank(""))
        XCTAssertGreaterThan(MessageStatus.rank(MessageStatus.sending),   MessageStatus.rank(MessageStatus.failed))
    }

    func testDeliveredCannotRegressToSent() {
        // The exact race scenario: the receipt arrives BEFORE the sender's
        // own "Sent" dispatch from the TCP-write completion. The late "Sent"
        // must not overwrite "Delivered".
        XCTAssertFalse(MessageStatus.shouldApply(MessageStatus.sent, over: MessageStatus.delivered))
        XCTAssertFalse(MessageStatus.shouldApply(MessageStatus.sent, over: MessageStatus.read))
    }

    func testReadCannotRegressToDelivered() {
        XCTAssertFalse(MessageStatus.shouldApply(MessageStatus.delivered, over: MessageStatus.read))
    }

    func testUpgradesAreAllowed() {
        XCTAssertTrue(MessageStatus.shouldApply(MessageStatus.sent,      over: ""))
        XCTAssertTrue(MessageStatus.shouldApply(MessageStatus.sent,      over: MessageStatus.sending))
        XCTAssertTrue(MessageStatus.shouldApply(MessageStatus.sent,      over: MessageStatus.queued))
        XCTAssertTrue(MessageStatus.shouldApply(MessageStatus.delivered, over: MessageStatus.sent))
        XCTAssertTrue(MessageStatus.shouldApply(MessageStatus.read,      over: MessageStatus.delivered))
    }

    func testFailedDoesNotOverwriteDelivered() {
        XCTAssertFalse(MessageStatus.shouldApply(MessageStatus.failed, over: MessageStatus.delivered))
        XCTAssertFalse(MessageStatus.shouldApply(MessageStatus.failed, over: MessageStatus.sent))
    }

    func testHistoryStoreUpdateStatusIsRankAware() {
        let entry = MessageEntry(
            sender: "me", text: "hi", incoming: false,
            timestamp: 1.0, messageId: "msg-rank-test",
            status: MessageStatus.sending, readReceiptSent: false
        )
        let peer = "192.168.99.99"
        HistoryStore.shared.append(entry: entry, forPeerIP: peer)

        // Simulate the race: receipt arrives first (Delivered), then the late
        // "Sent" dispatch from the sender's own send-completion.
        XCTAssertTrue(HistoryStore.shared.updateStatus(MessageStatus.delivered, forMessageId: "msg-rank-test", peerIP: peer))
        XCTAssertFalse(HistoryStore.shared.updateStatus(MessageStatus.sent,     forMessageId: "msg-rank-test", peerIP: peer))

        let stored = HistoryStore.shared.entries(forPeerIP: peer).first(where: { $0.messageId == "msg-rank-test" })
        XCTAssertEqual(stored?.status, MessageStatus.delivered,
            "Late 'Sent' dispatch must not overwrite 'Delivered' — this was the single-tick bug.")

        HistoryStore.shared.delete(peerIP: peer)
    }
}
