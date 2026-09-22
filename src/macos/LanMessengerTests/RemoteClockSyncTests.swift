import XCTest
@testable import LanMessenger

/// The arithmetic that makes a cross-machine latency figure mean anything.
///
/// It cannot be checked by looking at a running session — a wrong offset
/// produces a plausible-looking number, which is exactly how the 32.5-day
/// reading survived a whole evening of testing. So the cases are built from a
/// known ground truth and the answer is checked against it.
///
/// Mirror of `RemoteClockSyncTests.cs`.
final class RemoteClockSyncTests: XCTestCase {

    /// A round trip against a peer whose clock is `offset` microseconds ahead of
    /// ours, with the two legs taking `oneWay` each.
    private func roundTrip(_ sync: inout RemoteClockSync,
                           ourClockAtSend t1: UInt64,
                           peerAhead offset: Int64,
                           oneWayUs oneWay: UInt64,
                           peerDelayUs peerDelay: UInt64 = 0) -> Bool {
        let ourTimeAtTheirSend = t1 + oneWay + peerDelay
        let t3 = UInt64(Int64(ourTimeAtTheirSend) + offset)
        let t4 = t1 + oneWay + peerDelay + oneWay
        return sync.record(pingSentUs: t1, pongSentUs: t3, pongReceivedUs: t4)
    }

    func testAnOffsetIsRecoveredFromASymmetricRoundTrip() {
        // The ground truth: their clock is one second ahead of ours, each leg
        // takes 5ms. The estimate should be exact.
        var sync = RemoteClockSync()
        XCTAssertTrue(roundTrip(&sync, ourClockAtSend: 4_000_000,
                                peerAhead: 1_000_000, oneWayUs: 5_000))

        XCTAssertEqual(sync.offsetUs, 1_000_000)
        XCTAssertEqual(sync.rttUs, 10_000)
        XCTAssertTrue(sync.isSynced)
    }

    func testAPeerBehindUsGivesANegativeOffset() {
        // Both directions have to work: whichever machine booted first is an
        // accident, and an unsigned offset would fail on half the pairs.
        var sync = RemoteClockSync()
        roundTrip(&sync, ourClockAtSend: 9_000_000, peerAhead: -2_500_000, oneWayUs: 3_000)

        XCTAssertEqual(sync.offsetUs, -2_500_000)
    }

    func testTheFigureThisExistsForComesOutRight() {
        // The real reading from the first cross-machine session: a viewer
        // subtracting the host's capture_us from its own clock got about 32.5
        // days. With the offset measured, the same frame reads as 40ms.
        let peerAhead: Int64 = 2_813_817_000_000        // ~32.5 days
        var sync = RemoteClockSync()
        roundTrip(&sync, ourClockAtSend: 4_000_000, peerAhead: peerAhead, oneWayUs: 5_000)

        // A frame captured on their clock, presented 40ms later on ours.
        let ourTimeAtCapture: UInt64 = 4_500_000
        let captureUs = UInt64(Int64(ourTimeAtCapture) + peerAhead)
        let presentedAt: UInt64 = ourTimeAtCapture + 40_000

        guard let capturedOnOurClock = sync.toLocalUs(peerUs: captureUs) else {
            return XCTFail("a synced estimator refused to convert")
        }
        XCTAssertEqual(Int64(presentedAt) - capturedOnOurClock, 40_000)
    }

    func testTheBestSampleWinsRatherThanTheNewest() {
        // A sample's error is bounded by half its round trip, so the lowest-RTT
        // sample is the most trustworthy estimate ever taken. Letting a later,
        // slower one overwrite it is how a good measurement gets thrown away.
        var sync = RemoteClockSync()
        XCTAssertTrue(roundTrip(&sync, ourClockAtSend: 1_000_000,
                                peerAhead: 500_000, oneWayUs: 2_000))
        XCTAssertEqual(sync.rttUs, 4_000)

        // Slower, and asymmetric enough to be wrong: it must not be taken.
        XCTAssertFalse(sync.record(pingSentUs: 2_000_000,
                                   pongSentUs: 2_600_000,
                                   pongReceivedUs: 2_400_000))
        XCTAssertEqual(sync.offsetUs, 500_000, "a slower sample replaced a better one")
        XCTAssertEqual(sync.rttUs, 4_000)
        XCTAssertEqual(sync.samples, 2, "a rejected-for-quality sample still counts as seen")
    }

    func testABetterSampleDoesReplaceTheEstimate() {
        var sync = RemoteClockSync()
        roundTrip(&sync, ourClockAtSend: 1_000_000, peerAhead: 500_000, oneWayUs: 20_000)
        XCTAssertEqual(sync.rttUs, 40_000)

        XCTAssertTrue(roundTrip(&sync, ourClockAtSend: 3_000_000,
                                peerAhead: 500_000, oneWayUs: 1_000))
        XCTAssertEqual(sync.rttUs, 2_000)
        XCTAssertEqual(sync.offsetUs, 500_000)
    }

    func testAStallIsNotAMeasurement() {
        // The symmetry assumption is what bounds the error, and a round trip of
        // several seconds cannot have been symmetric. Taking it would put the
        // offset out by up to a second and every later latency with it.
        var sync = RemoteClockSync()
        XCTAssertFalse(sync.record(pingSentUs: 0,
                                   pongSentUs: 1_000_000,
                                   pongReceivedUs: 5_000_000))
        XCTAssertFalse(sync.isSynced)
        XCTAssertEqual(sync.rejected, 1)
        XCTAssertEqual(sync.samples, 0)
    }

    func testAPongFromBeforeItsPingIsRefused() {
        // Corrupt, replayed, or a mismatched id. Not a fast network.
        var sync = RemoteClockSync()
        XCTAssertFalse(sync.record(pingSentUs: 5_000, pongSentUs: 1_000, pongReceivedUs: 4_000))
        XCTAssertFalse(sync.isSynced)
        XCTAssertEqual(sync.rejected, 1)
    }

    func testAnUnsyncedEstimatorConvertsNothing() {
        // Falling back to the raw peer timestamp would be wrong by the entire
        // offset — which is the bug this whole type exists to end — so the
        // caller is told there is no answer instead.
        let sync = RemoteClockSync()
        XCTAssertNil(sync.toLocalUs(peerUs: 1_234_567))
        XCTAssertFalse(sync.isSynced)
        XCTAssertTrue(sync.summary.contains("unsynced"))
    }

    func testTheMidpointSurvivesClocksDaysApart() {
        // Two UInt64 microsecond clocks days apart overflow if their sum is
        // taken. The midpoint is computed from the round trip instead.
        var sync = RemoteClockSync()
        let huge: UInt64 = 18_000_000_000_000        // ~208 days of uptime
        XCTAssertTrue(sync.record(pingSentUs: huge,
                                  pongSentUs: 10,
                                  pongReceivedUs: huge + 10_000))
        XCTAssertEqual(sync.offsetUs, 10 - Int64(huge + 5_000))
    }
}
