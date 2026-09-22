import XCTest
@testable import LanMessenger

/// The protocol's two-frame rule, which PROTOCOL.md states once and five
/// independent places have to keep. Mirror of `VideoFrameBudgetTests.cs`.
final class VideoFrameBudgetTests: XCTestCase {

    func testTheThirdFrameIsRefused() {
        // One being worked on, one waiting. Fixed by the protocol, not tuned.
        let budget = VideoFrameBudget()
        XCTAssertTrue(budget.tryAcquire())
        XCTAssertTrue(budget.tryAcquire())
        XCTAssertFalse(budget.tryAcquire())
        XCTAssertEqual(budget.inFlight, 2)
        XCTAssertEqual(budget.refused, 1)
    }

    func testARefusalIsCountedRatherThanSwallowed() {
        // A budget that is constantly full is a real signal about the encoder.
        // Dropping silently is how that signal is lost — and "it feels laggy"
        // becomes the only available measurement again.
        let budget = VideoFrameBudget()
        _ = budget.tryAcquire(); _ = budget.tryAcquire()
        for _ in 0..<5 { XCTAssertFalse(budget.tryAcquire()) }
        XCTAssertEqual(budget.refused, 5)
        XCTAssertTrue(budget.summary.contains("refused=5"))
    }

    func testAReleasedSlotComesBack() {
        let budget = VideoFrameBudget()
        _ = budget.tryAcquire(); _ = budget.tryAcquire()
        budget.release()
        XCTAssertTrue(budget.tryAcquire())
        XCTAssertEqual(budget.inFlight, 2)
    }

    func testReleasingMoreThanWasTakenCannotWidenTheBudget() {
        // An encoder that emits two outputs for one input would otherwise drive
        // the count negative, and the budget would stop bounding anything at
        // all — a slow drift back into unbounded latency with nothing to see.
        let budget = VideoFrameBudget()
        _ = budget.tryAcquire()
        budget.release()
        budget.release()
        budget.release()
        XCTAssertEqual(budget.inFlight, 0)

        XCTAssertTrue(budget.tryAcquire())
        XCTAssertTrue(budget.tryAcquire())
        XCTAssertFalse(budget.tryAcquire(), "the cap held after an over-release")
    }

    func testResetForgetsWhatWillNeverComeBack() {
        // Flush, restart and teardown. A slot leaked across one of those shrinks
        // the budget for the rest of the session, and after two the encoder
        // accepts nothing with no error anywhere to explain it.
        let budget = VideoFrameBudget()
        _ = budget.tryAcquire(); _ = budget.tryAcquire()
        budget.reset()
        XCTAssertEqual(budget.inFlight, 0)
        XCTAssertTrue(budget.tryAcquire())
    }

    func testTheCapacityIsTheProtocolsNumber() {
        // Not a tuning knob. PROTOCOL.md fixes it, and both platforms say 2.
        XCTAssertEqual(VideoFrameBudget.protocolCapacity, 2)
        XCTAssertEqual(VideoFrameBudget().capacity, 2)
    }

    func testACodecPipelineGetsARunawayGuardRatherThanTheQueueRule() {
        // The mistake this encodes: two is right for a queue and wrong for a
        // hardware transform. Quick Sync issues one METransformNeedInput per
        // pipeline slot and emits nothing until enough are filled — capped at
        // two it produced no video at all, and not one encoder_stats line to
        // say why. A pipeline's depth is fixed latency, not growth.
        XCTAssertGreaterThan(VideoFrameBudget.pipelineCapacity,
                             VideoFrameBudget.protocolCapacity)

        let pipeline = VideoFrameBudget(capacity: VideoFrameBudget.pipelineCapacity)
        for _ in 0..<VideoFrameBudget.pipelineCapacity {
            XCTAssertTrue(pipeline.tryAcquire(), "a pipeline must not be starved")
        }
        XCTAssertFalse(pipeline.tryAcquire(), "but a runaway is still caught")
    }

    func testThePeakIsReportedBecauseNothingElseCanSeeIt() {
        // The codec's own depth is latency no other measurement reaches, and
        // exposing it is the point of the encoder's budget now that it is not
        // pacing anything.
        let budget = VideoFrameBudget(capacity: 8)
        for _ in 0..<5 { _ = budget.tryAcquire() }
        for _ in 0..<5 { budget.release() }

        XCTAssertEqual(budget.inFlight, 0)
        XCTAssertEqual(budget.highWater, 5)
        XCTAssertTrue(budget.summary.contains("peak=5"), budget.summary)
    }

    func testConcurrentAcquiresNeverExceedTheCap() {
        // The capture thread submits and the encoder's callback releases, on
        // different threads. A cap that only holds single-threaded is not a cap.
        let budget = VideoFrameBudget()
        let done = expectation(description: "all attempts finished")
        done.expectedFulfillmentCount = 8

        let peak = NSLock()
        var highWater = 0

        for _ in 0..<8 {
            DispatchQueue.global().async {
                for _ in 0..<500 {
                    if budget.tryAcquire() {
                        peak.lock()
                        highWater = max(highWater, budget.inFlight)
                        peak.unlock()
                        budget.release()
                    }
                }
                done.fulfill()
            }
        }

        wait(for: [done], timeout: 20)
        XCTAssertLessThanOrEqual(highWater, VideoFrameBudget.protocolCapacity)
        XCTAssertEqual(budget.inFlight, 0)
    }
}
