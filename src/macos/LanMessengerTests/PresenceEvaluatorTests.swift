import XCTest
@testable import LanMessenger

// Guards the LAN presence state machine. See PresenceEvaluator.swift.
//
// The bug this replaces: presence was `now - lastSeen < 20s`, with no graceful
// "goodbye" and no active probing — peers lingered "online" for up to 20 s
// after quitting, and shortening the window reintroduced UDP-loss flicker.
final class PresenceEvaluatorTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_000_000)

    private func seenAgo(_ seconds: TimeInterval) -> Date {
        now.addingTimeInterval(-seconds)
    }

    func testFreshHeartbeatIsOnline() {
        let d = PresenceEvaluator.decide(lastSeen: seenAgo(0.5), now: now)
        XCTAssertEqual(d, .online)
        XCTAssertEqual(d.presence, .online)
        XCTAssertFalse(d.shouldProbe)
    }

    func testJustInsideGraceIsOnline() {
        // ~3 beacons of slack — a couple of dropped beacons must not flip state.
        let d = PresenceEvaluator.decide(lastSeen: seenAgo(PresenceEvaluator.onlineGrace - 0.1), now: now)
        XCTAssertEqual(d, .online)
    }

    func testStalePeerProbesButStaysOnline() {
        // Between the grace edge and the hard cap: still shown online, but the
        // evaluator wants a liveness probe so a quiet-but-alive peer is
        // reconfirmed instead of being declared offline.
        let d = PresenceEvaluator.decide(lastSeen: seenAgo(PresenceEvaluator.onlineGrace + 1), now: now)
        XCTAssertEqual(d, .probing)
        XCTAssertEqual(d.presence, .online, "probing is a grace window — must still display online")
        XCTAssertTrue(d.shouldProbe)
    }

    func testSilentPastHardCapIsOffline() {
        let d = PresenceEvaluator.decide(lastSeen: seenAgo(PresenceEvaluator.offlineAfter + 1), now: now)
        XCTAssertEqual(d, .offline)
        XCTAssertEqual(d.presence, .offline)
        XCTAssertFalse(d.shouldProbe)
    }

    func testBoundaryAtOfflineAfterIsOffline() {
        // Exactly at the cap is offline (the probing window is half-open).
        let d = PresenceEvaluator.decide(lastSeen: seenAgo(PresenceEvaluator.offlineAfter), now: now)
        XCTAssertEqual(d, .offline)
    }

    func testWindowsAreOrderedAndShorterThanLegacyTimeout() {
        // Sanity on the tunables: grace < cap, and the whole machine resolves
        // well before the old 20 s timeout that felt broken.
        XCTAssertLessThan(PresenceEvaluator.onlineGrace, PresenceEvaluator.offlineAfter)
        XCTAssertLessThan(PresenceEvaluator.offlineAfter, 20)
    }
}
