import XCTest
import CoreVideo
import CoreGraphics
@testable import LanMessenger

// The one thing about screen capture that cannot be asserted without a granted
// TCC permission: that `SCStream` delivers a frame at all.
//
// Everything else about `ScreenCaptureSource` — geometry, the configuration
// mapping, frame-status gating, the capture clock, restart backoff — is pure and
// covered by `ScreenCaptureSourceTests`. This is the remaining gap, and it needs
// a human, because Screen Recording is a TCC grant keyed to the code signature
// of whatever is running the tests.
//
// **To run it:**
//
//   1. System Settings → Privacy & Security → Screen Recording → enable the app
//      that runs your tests (Terminal, iTerm, Xcode — whichever you use).
//      You will be asked to quit and reopen it. Do that, or the grant does not
//      take effect for the already-running process.
//   2. cd src/macos && LANMSG_LIVE_CAPTURE=1 swift test --filter ScreenCaptureLiveTests
//   3. **Move the mouse or drag a window while it runs.** SCStream is
//      change-driven: a completely still screen legitimately delivers nothing,
//      so "no frames" on a static desktop proves nothing either way. The test
//      says so as it runs.
//
// It is skipped by default, like the fixture and render generators, because a
// test that needs a permission and a human is not one CI can run.
final class ScreenCaptureLiveTests: XCTestCase {

    private var isEnabled: Bool {
        ProcessInfo.processInfo.environment["LANMSG_LIVE_CAPTURE"] == "1"
    }

    func testCaptureDeliversRealFrames() async throws {
        try XCTSkipUnless(isEnabled,
                          "set LANMSG_LIVE_CAPTURE=1 to run the live capture check")

        guard CGPreflightScreenCaptureAccess() else {
            XCTFail("""
                Screen Recording is not granted to the process running these tests.

                System Settings → Privacy & Security → Screen Recording, enable
                your terminal or Xcode, then QUIT AND REOPEN it — the grant does
                not apply to an already-running process.
                """)
            return
        }

        let source = ScreenCaptureSource()
        let lock = NSLock()
        var frames = 0
        var firstSize: (width: Int, height: Int)?
        var timestamps: [UInt64] = []

        source.onFrame = { pixelBuffer, captureUs in
            lock.lock()
            frames += 1
            if firstSize == nil {
                firstSize = (CVPixelBufferGetWidth(pixelBuffer),
                             CVPixelBufferGetHeight(pixelBuffer))
            }
            timestamps.append(captureUs)
            lock.unlock()
        }
        source.onFatalError = { error in XCTFail("capture failed: \(error)") }

        try await source.start()
        XCTAssertTrue(source.isRunning)

        let size = try XCTUnwrap(source.currentSize, "capture started without a size")
        print("""

            ── live capture running ─────────────────────────────────────────
              configured \(size.width)x\(size.height)
              MOVE THE MOUSE or drag a window for the next 15 seconds.
              SCStream is change-driven: a still screen sends nothing, and that
              is correct behaviour rather than a failure.
            ─────────────────────────────────────────────────────────────────

            """)

        // Fifteen seconds is generous. A moving cursor alone produces frames
        // when showsCursor is on, which it is by default.
        for _ in 0..<15 {
            try await Task.sleep(nanoseconds: 1_000_000_000)
            lock.lock(); let count = frames; lock.unlock()
            if count >= 10 { break }
        }
        source.stop()

        lock.lock()
        let total = frames
        let delivered = firstSize
        let clocks = timestamps
        lock.unlock()

        print("captured \(total) frame(s)")
        if let delivered { print("first frame \(delivered.width)x\(delivered.height)") }

        XCTAssertGreaterThan(total, 0, """
            No frames arrived in 15 seconds.

            If the screen was genuinely still that is expected — run it again and
            move the mouse. If you were moving the mouse, this is the failure the
            whole WS4a verification exists to catch, and the `remote` log channel
            will say what SCStream reported.
            """)

        // The picture must match what the encoder was told to expect. A capture
        // that silently delivers a different size than `currentSize` would have
        // the encoder rejecting every frame.
        if let delivered {
            XCTAssertEqual(delivered.width, size.width, "delivered width != configured width")
            XCTAssertEqual(delivered.height, size.height, "delivered height != configured height")
            XCTAssertEqual(delivered.width % 2, 0, "H.264 needs even dimensions")
            XCTAssertEqual(delivered.height % 2, 0, "H.264 needs even dimensions")
        }

        // Capture timestamps must advance. A stuck clock makes every latency
        // figure nonsense and is invisible in a single frame.
        XCTAssertTrue(clocks.allSatisfy { $0 > 0 }, "a frame arrived with no capture time")
        if clocks.count >= 2 {
            XCTAssertEqual(clocks, clocks.sorted(), "capture timestamps went backwards")
            XCTAssertGreaterThan(clocks.last! - clocks.first!, 0,
                                 "the capture clock never advanced")
        }
    }

    /// Captures a few frames and pushes them through the real encoder, which is
    /// the first time anything in this project encodes an actual screen.
    func testCapturedFramesEncodeToH264() async throws {
        try XCTSkipUnless(isEnabled,
                          "set LANMSG_LIVE_CAPTURE=1 to run the live capture check")
        guard CGPreflightScreenCaptureAccess() else {
            throw XCTSkip("Screen Recording not granted; run testCaptureDeliversRealFrames first")
        }

        let source = ScreenCaptureSource()
        try await source.start()
        let size = try XCTUnwrap(source.currentSize)

        let pipeline = try VideoSendPipeline(
            dimensions: size, bitrate: 6_000_000, frameRate: 30,
            submit: { _ in .queued })

        source.onFrame = { pixelBuffer, captureUs in
            pipeline.encode(pixelBuffer: pixelBuffer, captureUs: captureUs)
        }

        print("\n── encoding live screen content for 10 seconds; move the mouse ──\n")
        for _ in 0..<10 {
            try await Task.sleep(nanoseconds: 1_000_000_000)
            if pipeline.stats.framesEncoded >= 10 { break }
        }
        source.stop()
        pipeline.stop()

        let stats = pipeline.stats
        print("encoded \(stats.framesEncoded) frame(s), "
              + "\(stats.keyframesSubmitted) keyframe(s), \(stats.bytesSubmitted) bytes")

        XCTAssertGreaterThan(stats.framesEncoded, 0, """
            Frames were captured but none encoded. This is the capture-to-encoder
            seam, and it has never run against a real screen before.
            """)
        XCTAssertGreaterThan(stats.keyframesSubmitted, 0, "a stream must open with an IDR")
        XCTAssertGreaterThan(stats.bytesSubmitted, 0)
    }
}
