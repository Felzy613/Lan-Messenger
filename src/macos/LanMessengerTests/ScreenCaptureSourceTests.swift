import XCTest
import CoreMedia
import CoreVideo
import CoreGraphics
import ScreenCaptureKit
@testable import LanMessenger

// What can be asserted about screen capture on a machine with no Screen
// Recording grant — which is every CI runner, and this development machine.
//
// So the seams are drawn to make the decisions testable and leave only the
// stream lifecycle untestable: geometry resolution, the `SCStreamConfiguration`
// mapping, the frame-status gate, the capture clock, and the restart backoff are
// all reachable without a display. What is *not* covered here, and cannot be, is
// that `SCStream` delivers a frame at all — that needs a granted, signed app and
// someone at the keyboard. See docs/REMOTE_DESKTOP.md → WS4a.
final class ScreenCaptureSourceTests: XCTestCase {

    // MARK: - Geometry

    func testNonRetinaDisplayCapturesAtItsPointSize() {
        let size = ScreenCaptureGeometry.pixelSize(
            pointWidth: 1280, pointHeight: 800, backingScale: 1, maxLongEdge: nil)
        XCTAssertEqual(size.width, 1280)
        XCTAssertEqual(size.height, 800)
    }

    func testRetinaScaleIsAppliedBecauseSCDisplayReportsPoints() {
        // `SCDisplay` reports points and `SCStreamConfiguration` takes pixels,
        // and nothing in ScreenCaptureKit bridges the two. Missing this captures
        // a quarter of the panel's detail and nothing says so.
        let size = ScreenCaptureGeometry.pixelSize(
            pointWidth: 1440, pointHeight: 900, backingScale: 2, maxLongEdge: nil)
        XCTAssertEqual(size.width, 2880)
        XCTAssertEqual(size.height, 1800)
    }

    func testOversizeDisplayIsCappedOnItsLongEdgeAndKeepsItsAspect() {
        let size = ScreenCaptureGeometry.pixelSize(
            pointWidth: 1440, pointHeight: 900, backingScale: 2, maxLongEdge: 1920)
        XCTAssertEqual(size.width, 1920)
        XCTAssertEqual(size.height, 1200)

        // A 16:10 panel must stay 16:10; letterboxing the far side is a bug the
        // viewer cannot correct, because it is told the wrong dimensions.
        XCTAssertEqual(Double(size.width) / Double(size.height), 1.6, accuracy: 0.01)
    }

    func testTallDisplayIsCappedOnItsOwnLongEdge() {
        // A rotated monitor. Capping width rather than the long edge silently
        // upscales a portrait display.
        let size = ScreenCaptureGeometry.pixelSize(
            pointWidth: 1080, pointHeight: 1920, backingScale: 1, maxLongEdge: 960)
        XCTAssertEqual(size.height, 960)
        XCTAssertEqual(size.width, 540)
    }

    func testDimensionsAreAlwaysEven() {
        // H.264 4:2:0 subsamples chroma by two. An odd dimension is not
        // rejected — the encoder rounds it quietly, and the viewer then lays out
        // against a picture one pixel wider than the one it is being sent.
        for width in 1...64 {
            for scale in [1.0, 1.5, 2.0] {
                let size = ScreenCaptureGeometry.pixelSize(
                    pointWidth: width, pointHeight: width * 3, backingScale: scale,
                    maxLongEdge: nil)
                XCTAssertEqual(size.width % 2, 0, "width \(size.width) from \(width)@\(scale)")
                XCTAssertEqual(size.height % 2, 0, "height \(size.height) from \(width)@\(scale)")
                XCTAssertGreaterThanOrEqual(size.width, 2)
                XCTAssertGreaterThanOrEqual(size.height, 2)
            }
        }
    }

    func testDegenerateInputsDoNotProduceAZeroSizedCapture() {
        // A zero-sized encoder session fails at creation, which would turn a
        // transient display glitch into a dead session.
        let size = ScreenCaptureGeometry.pixelSize(
            pointWidth: 0, pointHeight: 0, backingScale: 0, maxLongEdge: 0)
        XCTAssertEqual(size.width, 2)
        XCTAssertEqual(size.height, 2)
    }

    // MARK: - Stream configuration

    func testStreamConfigurationMatchesTheEncodersColourChoices() {
        // These have to agree with H264Encoder. Capturing full-range pixels into
        // a video-range encoder is how blacks come out washed out and whites
        // clipped on the far side, and neither component complains.
        let configuration = ScreenCaptureSource.makeStreamConfiguration(
            ScreenCaptureConfiguration(), width: 1920, height: 1200)

        XCTAssertEqual(configuration.pixelFormat, kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange)
        XCTAssertEqual(configuration.colorMatrix, kCVImageBufferYCbCrMatrix_ITU_R_709_2)
        XCTAssertEqual(configuration.colorSpaceName, CGColorSpace.itur_709)
        XCTAssertEqual(configuration.width, 1920)
        XCTAssertEqual(configuration.height, 1200)
    }

    func testFrameIntervalIsACeilingAtTheRequestedRate() {
        var intent = ScreenCaptureConfiguration()
        intent.frameRate = 30
        let configuration = ScreenCaptureSource.makeStreamConfiguration(
            intent, width: 640, height: 480)

        XCTAssertEqual(CMTimeGetSeconds(configuration.minimumFrameInterval), 1.0 / 30.0,
                       accuracy: 0.0001)
    }

    func testQueueDepthStaysShallowAndNeverDegenerate() {
        var intent = ScreenCaptureConfiguration()
        intent.queueDepth = 3
        XCTAssertEqual(ScreenCaptureSource.makeStreamConfiguration(
            intent, width: 8, height: 8).queueDepth, 3)

        // Every slot is a frame of latency the viewer can never pay back, but
        // zero is not a valid depth.
        intent.queueDepth = 0
        XCTAssertEqual(ScreenCaptureSource.makeStreamConfiguration(
            intent, width: 8, height: 8).queueDepth, 2)
    }

    func testCursorIsCompositedByDefault() {
        // The opposite of DXGI Desktop Duplication, which excludes the pointer
        // and hands the shape over separately. v1 composites on both platforms
        // so a viewer sees one thing.
        XCTAssertTrue(ScreenCaptureSource.makeStreamConfiguration(
            ScreenCaptureConfiguration(), width: 8, height: 8).showsCursor)

        var hidden = ScreenCaptureConfiguration()
        hidden.showsCursor = false
        XCTAssertFalse(ScreenCaptureSource.makeStreamConfiguration(
            hidden, width: 8, height: 8).showsCursor)
    }

    // MARK: - Frame gating

    func testCompleteFramesAreDelivered() throws {
        let sample = try makeSampleBuffer(status: .complete, presentationUs: 123_456)
        let frame = try XCTUnwrap(ScreenCaptureSource.deliverableFrame(from: sample))
        XCTAssertEqual(frame.captureUs, 123_456)
        XCTAssertEqual(CVPixelBufferGetWidth(frame.pixelBuffer), 64)
    }

    func testIdleAndBlankFramesAreDropped() throws {
        // SCK delivers these carrying whatever pixels were in the pool.
        // Encoding one emits a perfectly valid picture of the wrong thing — a
        // stale frame, or a black one, with no error anywhere.
        for status in [SCFrameStatus.idle, .blank, .suspended, .stopped] {
            let sample = try makeSampleBuffer(status: status, presentationUs: 1_000)
            XCTAssertNil(ScreenCaptureSource.deliverableFrame(from: sample),
                         "\(status) was delivered as a picture")
        }
    }

    func testAMissingStatusIsTreatedAsAPicture() throws {
        // Deliberately lenient. SCK always sets the status today, but dropping
        // on a *missing* one would mean a future SCK that stopped setting it
        // produced no video at all, silently — and "no picture, no error" is the
        // worst failure this feature has. One stale frame is an artefact; no
        // frames is indistinguishable from a crash.
        let sample = try makeSampleBuffer(status: nil, presentationUs: 42)
        XCTAssertNotNil(ScreenCaptureSource.deliverableFrame(from: sample))
    }

    // MARK: - The capture clock

    func testCaptureTimeComesFromTheSampleNotFromDelivery() throws {
        // The difference between the two is the time the frame spent in SCK's
        // pool, which is exactly the latency the measurement exists to catch.
        let sample = try makeSampleBuffer(status: .complete, presentationUs: 9_876_543)
        XCTAssertEqual(ScreenCaptureSource.captureMicroseconds(of: sample), 9_876_543)
    }

    func testAnUnusableTimestampFallsBackToTheHostClock() throws {
        // A zero presentation time would otherwise put every frame at the epoch
        // and make the latency figure nonsense rather than absent.
        let sample = try makeSampleBuffer(status: .complete, presentationUs: 0)
        let captured = ScreenCaptureSource.captureMicroseconds(of: sample)
        XCTAssertGreaterThan(captured, 0)

        let hostNow = CMTimeConvertScale(CMClockGetTime(CMClockGetHostTimeClock()),
                                         timescale: 1_000_000, method: .roundHalfAwayFromZero)
        XCTAssertEqual(Double(captured), Double(hostNow.value), accuracy: 2_000_000,
                       "the fallback must be the host clock, not an arbitrary origin")
    }

    // MARK: - Restart

    func testRestartBackoffClimbsAndThenHolds() {
        let policy = CaptureRestartPolicy()
        XCTAssertEqual(policy.delay(forAttempt: 0), 0.25)
        XCTAssertEqual(policy.delay(forAttempt: 3), 2.0)

        // Bounded delay, unbounded attempts. A display that went away because
        // the lid is shut comes back minutes later, and a host that gave up in
        // the meantime is indistinguishable from a crashed one.
        XCTAssertEqual(policy.delay(forAttempt: 4), 5.0)
        XCTAssertEqual(policy.delay(forAttempt: 500), 5.0)
        XCTAssertEqual(policy.delay(forAttempt: -1), 0.25, "a negative attempt must not trap")
    }

    func testEmptyBackoffScheduleStillYieldsAUsableDelay() {
        XCTAssertEqual(CaptureRestartPolicy(delays: []).delay(forAttempt: 0), 1)
    }

    // MARK: - Permission

    func testStartRefusesWithoutAGrantAndDoesNotPromptForOne() async throws {
        try XCTSkipIf(CGPreflightScreenCaptureAccess(),
                      "this machine has the Screen Recording grant; the refusal path cannot run")

        // Preflight only. Requesting belongs to the consent flow, where a human
        // is already being asked something — raising a system prompt from here
        // would mean an incoming invite could pop a permission dialog on a host
        // that has not agreed to anything yet.
        let source = ScreenCaptureSource()
        do {
            try await source.start()
            XCTFail("start() succeeded without a Screen Recording grant")
        } catch ScreenCaptureError.permissionDenied {
            XCTAssertFalse(source.isRunning)
            XCTAssertNil(source.currentSize)
        }
    }

    // MARK: - Helpers

    /// A sample buffer shaped like one SCK delivers: an image buffer, a
    /// presentation time, and a frame status in the sample attachments.
    private func makeSampleBuffer(status: SCFrameStatus?, presentationUs: Int64) throws -> CMSampleBuffer {
        var pixelBuffer: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(
            kCFAllocatorDefault, 64, 64,
            kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange, nil, &pixelBuffer), kCVReturnSuccess)
        let buffer = try XCTUnwrap(pixelBuffer)

        var formatDescription: CMFormatDescription?
        XCTAssertEqual(CMVideoFormatDescriptionCreateForImageBuffer(
            allocator: kCFAllocatorDefault, imageBuffer: buffer,
            formatDescriptionOut: &formatDescription), noErr)

        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: CMTime(value: CMTimeValue(presentationUs), timescale: 1_000_000),
            decodeTimeStamp: .invalid)

        var sampleBuffer: CMSampleBuffer?
        XCTAssertEqual(CMSampleBufferCreateReadyWithImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: buffer,
            formatDescription: try XCTUnwrap(formatDescription),
            sampleTiming: &timing,
            sampleBufferOut: &sampleBuffer), noErr)
        let sample = try XCTUnwrap(sampleBuffer)

        if let status {
            let attachments = try XCTUnwrap(CMSampleBufferGetSampleAttachmentsArray(
                sample, createIfNecessary: true))
            XCTAssertGreaterThan(CFArrayGetCount(attachments), 0)
            let dictionary = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0),
                                           to: CFMutableDictionary.self)
            let key = SCStreamFrameInfo.status.rawValue as CFString
            let value = NSNumber(value: status.rawValue)
            CFDictionarySetValue(dictionary,
                                 Unmanaged.passUnretained(key).toOpaque(),
                                 Unmanaged.passUnretained(value).toOpaque())
        }
        return sample
    }
}
