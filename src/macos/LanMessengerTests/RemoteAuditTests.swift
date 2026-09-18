import XCTest
import SwiftUI
import AppKit
@testable import LanMessenger

// The audit trail: "session start, stop, and every control grant are recorded in
// the conversation history".
//
// It is the least glamorous requirement in the feature and probably the most
// useful — the question it answers is "was my screen shared last Tuesday, and
// who was watching", asked weeks later by somebody who is not going to read a
// log file. So it lives in the thread, where a user already looks to find out
// what happened between them and that peer.
//
// It reuses the `__FILE__:` marker-prefix trick, which keeps the stored history
// format unchanged at the cost of every call site that inspects `text` needing
// to know one more prefix. Several of these tests exist to make sure none of
// them forgets.
final class RemoteAuditTests: XCTestCase {

    private func entry(_ event: RemoteAuditEntry.Event,
                       reason: String? = nil,
                       duration: Double? = nil) -> RemoteAuditEntry {
        RemoteAuditEntry(event: event, peerName: "Dave Felzy",
                         reason: reason, duration: duration)
    }

    // MARK: - Storage

    func testARecordSurvivesARoundTrip() {
        for event in RemoteAuditEntry.Event.allCases {
            let original = entry(event, reason: "user_stopped", duration: 90)
            let decoded = RemoteAuditEntry.decode(original.encoded())
            XCTAssertEqual(decoded, original, "\(event) did not survive storage")
        }
    }

    func testEncodingIsStable() {
        // Sorted keys, so a record round-trips to the same bytes and a diff of
        // stored history is readable rather than a reordering.
        let record = entry(.sessionEnded, reason: "watchdog", duration: 12)
        XCTAssertEqual(record.encoded(), record.encoded())
        XCTAssertTrue(record.encoded().hasPrefix(RemoteAuditEntry.marker))
    }

    func testOrdinaryMessagesAreNotMistakenForAuditRecords() {
        // The failure this guards is loud and embarrassing: a message rendered
        // as a system row, or worse, swallowed entirely.
        XCTAssertNil(RemoteAuditEntry.decode("hello"))
        XCTAssertNil(RemoteAuditEntry.decode("__FILE__:/tmp/a.png"))
        XCTAssertNil(RemoteAuditEntry.decode(""))
        XCTAssertFalse(RemoteAuditEntry.isAudit("I sent __REMOTE__: by accident"))
    }

    func testAMalformedRecordIsIgnoredRatherThanFatal() {
        // A record written by a newer build, or a corrupted one, must not take
        // the conversation down with it.
        XCTAssertNil(RemoteAuditEntry.decode("__REMOTE__:not json"))
        XCTAssertNil(RemoteAuditEntry.decode("__REMOTE__:{}"))
        XCTAssertNil(RemoteAuditEntry.decode(
            #"__REMOTE__:{"event":"teleported","peerName":"Dave"}"#),
            "an unknown event must decode to nothing, not crash")
    }

    // MARK: - What it says

    func testEachEventReadsAsADistinctFact() {
        let summaries = RemoteAuditEntry.Event.allCases.map { entry($0).summary }
        XCTAssertEqual(Set(summaries).count, summaries.count,
                       "two events read identically in the trail")
        for summary in summaries {
            XCTAssertTrue(summary.contains("Dave Felzy") || summary.contains("Screen sharing"),
                          "\(summary) does not say who or what")
            XCTAssertFalse(summary.contains("_"), "a wire token leaked into the trail")
        }
    }

    func testTheEndReasonBecomesProse() {
        // The stored record keeps the token so it survives a change of wording;
        // the line shown is the sentence.
        XCTAssertEqual(entry(.sessionEnded, reason: "screen_locked").summary,
                       RemoteStopReason.screenLocked.auditDescription)
        XCTAssertEqual(entry(.sessionEnded, reason: "watchdog").summary,
                       RemoteStopReason.watchdog.auditDescription)
    }

    func testAnUnknownEndReasonStillProducesASentence() {
        let summary = entry(.sessionEnded, reason: "some_future_reason").summary
        XCTAssertTrue(summary.contains("Dave Felzy"))
        XCTAssertFalse(summary.contains("some_future_reason"))
    }

    func testDurationIsReportedInUnitsAPersonUses() {
        XCTAssertEqual(entry(.sessionEnded, duration: 7).durationSummary, "Lasted 7s.")
        XCTAssertEqual(entry(.sessionEnded, duration: 90).durationSummary, "Lasted 1m 30s.")
        XCTAssertEqual(entry(.sessionEnded, duration: 3700).durationSummary, "Lasted 1h 1m.")
    }

    func testAnInstantSessionReportsNoDuration() {
        // "Lasted 0s" is noise on a session that was declined or failed to set up.
        XCTAssertNil(entry(.sessionEnded, duration: 0).durationSummary)
        XCTAssertNil(entry(.sessionStarted).durationSummary)
    }

    // MARK: - As a history entry

    func testAnAuditRecordDoesNotCountAsAnUnreadMessage() {
        // Incoming entries drive the unread badge and the read-receipt path. An
        // audit record is a note about what happened, not something the peer
        // said, and a badge for it would be unexplainable.
        let historyEntry = entry(.sessionStarted).historyEntry()
        XCTAssertFalse(historyEntry.incoming)
        XCTAssertTrue(historyEntry.readReceiptSent)
        XCTAssertNil(historyEntry.messageId,
                     "no message id: there is no packet this corresponds to")
    }

    func testTheStoredEntryIsRecognisableAsAudit() {
        let historyEntry = entry(.controlGranted).historyEntry()
        XCTAssertTrue(RemoteAuditEntry.isAudit(historyEntry.text))
        XCTAssertEqual(RemoteAuditEntry.decode(historyEntry.text)?.event, .controlGranted)
    }

    func testTheTimestampIsCarriedThrough() {
        let when = Date(timeIntervalSince1970: 1_700_000_000)
        XCTAssertEqual(entry(.sessionStarted).historyEntry(at: when).timestamp,
                       when.timeIntervalSince1970, accuracy: 0.001)
    }

    // MARK: - The call sites that inspect message text

    func testEveryTextPrefixConventionIsDistinct() {
        // Three places inspect message text for a prefix — the sidebar preview,
        // the editability guard, and the chat row builder — and a collision
        // between conventions would route an attachment through the audit path
        // or vice versa.
        XCTAssertNotEqual(RemoteAuditEntry.marker, "__FILE__:")
        XCTAssertFalse(RemoteAuditEntry.marker.hasPrefix("__FILE__:"))
        XCTAssertFalse("__FILE__:".hasPrefix(RemoteAuditEntry.marker))
    }
}

/// Renders the audit rows as they appear in a thread. Skipped unless
/// `LANMSG_RENDER_UI=<dir>` is set.
@MainActor
final class RemoteAuditRenderTests: XCTestCase {

    func testRenderAuditRowsForReview() throws {
        guard let directory = ProcessInfo.processInfo.environment["LANMSG_RENDER_UI"] else {
            throw XCTSkip("set LANMSG_RENDER_UI=<dir> to regenerate the audit renders")
        }
        let when = Date(timeIntervalSince1970: 1_700_000_000)
        let rows: [RemoteAuditEntry] = [
            RemoteAuditEntry(event: .sessionStarted, peerName: "Dave Felzy"),
            RemoteAuditEntry(event: .controlGranted, peerName: "Dave Felzy"),
            RemoteAuditEntry(event: .controlRevoked, peerName: "Dave Felzy"),
            RemoteAuditEntry(event: .sessionEnded, peerName: "Dave Felzy",
                             reason: "kill_switch", duration: 372),
        ]

        let stack = VStack(spacing: 2) {
            ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                RemoteAuditRowView(entry: row, timestamp: when)
            }
        }
        .frame(width: 460)
        .padding(20)

        let renderer = ImageRenderer(content: stack)
        renderer.scale = 2
        let image = try XCTUnwrap(renderer.nsImage)
        guard let tiff = image.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:]) else {
            XCTFail("could not encode the audit rows"); return
        }
        let url = URL(fileURLWithPath: directory).appendingPathComponent("audit-rows.png")
        try png.write(to: url)
        print("wrote \(url.path)")
    }
}
