import Foundation
import AppKit
import CoreMedia
import CoreVideo
import CoreGraphics
import ScreenCaptureKit

// Continuous screen capture for the remote-desktop host path.
//
// This is not the screenshot path. `ScreenshotService` starts a stream, takes
// exactly one frame and abandons teardown on purpose; a session here runs for
// minutes or hours and has to survive everything the display system does to it
// in that time. The two differ in almost every decision, and the differences are
// marked where they occur.
//
// Three properties of `SCStream` shape the whole design, and each one has
// already cost somebody a day somewhere:
//
//  * **It is change-driven, not 30 fps.** `minimumFrameInterval` is a ceiling,
//    not a rate. A screen with nothing moving on it delivers *no frames at all*,
//    indefinitely, and that is correct behaviour. Nothing downstream may treat
//    silence as failure — not a watchdog, not a viewer, not a reconnect timer.
//    It is also why `capture_us` is carried per frame rather than inferred.
//  * **It stops silently.** A display reconfiguration, a resolution change, a
//    GPU reset or a user revoking the TCC grant all arrive as
//    `didStopWithError` and then nothing, forever. Restarting is mandatory, and
//    the restart has to re-enumerate shareable content because the display it
//    was capturing may no longer exist.
//  * **Frames are not all pictures.** SCK marks idle, blank and suspended
//    frames in the sample attachments. Encoding one produces a plausible
//    bitstream of the wrong thing, so only `.complete` frames are delivered.

enum ScreenCaptureError: Error, CustomStringConvertible {
    case permissionDenied
    case noDisplay(CGDirectDisplayID?)
    case streamFailed(String)

    var description: String {
        switch self {
        case .permissionDenied:
            return "Screen Recording permission is not granted"
        case .noDisplay(let id):
            return id.map { "display \($0) is not available" } ?? "no display available"
        case .streamFailed(let detail):
            return "screen capture failed: \(detail)"
        }
    }
}

/// What to capture. Pixel dimensions are resolved by `ScreenCaptureGeometry`
/// before a stream is built, so this carries intent rather than a size.
struct ScreenCaptureConfiguration {
    /// nil captures the main display.
    var displayID: CGDirectDisplayID?
    var frameRate: Int = 30
    /// SCK composites the pointer into the frame when true. This is the opposite
    /// of DXGI Desktop Duplication, which excludes it and hands you the shape
    /// separately — v1 composites on both platforms so a viewer sees one thing.
    var showsCursor: Bool = true
    /// Longest edge in pixels, or nil for the display's native size. A Retina
    /// 5K panel is 14.7 megapixels; sending it untouched spends the entire
    /// bitrate on pixels the viewer's window cannot show.
    var maxLongEdge: Int? = 1920
    /// SCK's frame pool. Small on purpose: every slot is a frame of latency the
    /// viewer can never pay back.
    var queueDepth: Int = 3

    init(displayID: CGDirectDisplayID? = nil) { self.displayID = displayID }
}

/// Pixel geometry, kept pure so the rounding rules are testable without a
/// display attached.
enum ScreenCaptureGeometry {

    /// Resolves a display's point size and backing scale to the pixel size a
    /// capture should use.
    ///
    /// Both axes come back **even**. H.264 4:2:0 subsamples chroma by two, and
    /// an odd dimension is not rejected — the encoder quietly rounds it, and the
    /// viewer then lays out against a size one pixel wider than the picture it
    /// is being sent.
    static func pixelSize(
        pointWidth: Int,
        pointHeight: Int,
        backingScale: Double,
        maxLongEdge: Int?
    ) -> (width: Int, height: Int) {
        let scale = backingScale > 0 ? backingScale : 1
        var width = Double(max(pointWidth, 1)) * scale
        var height = Double(max(pointHeight, 1)) * scale

        if let maxLongEdge, maxLongEdge > 0 {
            let longEdge = max(width, height)
            if longEdge > Double(maxLongEdge) {
                let factor = Double(maxLongEdge) / longEdge
                width *= factor
                height *= factor
            }
        }
        return (even(width), even(height))
    }

    private static func even(_ value: Double) -> Int {
        let rounded = Int(value.rounded())
        return max(2, rounded - (rounded % 2))
    }

    /// The backing scale of the screen showing `displayID`, or 1 when it cannot
    /// be resolved. `SCDisplay` reports points; `SCStreamConfiguration` wants
    /// pixels, and nothing in ScreenCaptureKit bridges the two.
    @MainActor
    static func backingScale(for displayID: CGDirectDisplayID) -> Double {
        for screen in NSScreen.screens {
            let number = screen.deviceDescription[
                NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
            if number?.uint32Value == displayID { return Double(screen.backingScaleFactor) }
        }
        return Double(NSScreen.main?.backingScaleFactor ?? 1)
    }
}

/// Backoff for capture restarts. Pure, so the schedule is asserted rather than
/// waited out.
struct CaptureRestartPolicy: Equatable {
    /// Deliberately unbounded in attempts and bounded in delay. A display that
    /// has gone away because the lid is shut comes back minutes later, and a
    /// host that gave up in the meantime looks identical to a crashed one.
    var delays: [TimeInterval] = [0.25, 0.5, 1.0, 2.0, 5.0]

    func delay(forAttempt attempt: Int) -> TimeInterval {
        guard !delays.isEmpty else { return 1 }
        return delays[min(max(attempt, 0), delays.count - 1)]
    }
}

final class ScreenCaptureSource: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {

    /// Delivered on `frameQueue`, never on main.
    ///
    /// Hopping to the main actor here would be wrong twice over: it adds a
    /// scheduling hop to every frame of a latency-critical path, and the pixel
    /// buffer belongs to SCK's pool of `queueDepth` slots — holding one past the
    /// callback starves the stream that produced it. Consume it synchronously.
    /// This is the deliberate exception to the "delegate callbacks hop to main"
    /// rule, which exists for UI state, and there is none here.
    var onFrame: ((CVPixelBuffer, UInt64) -> Void)?

    /// The stream was rebuilt. The caller must force an IDR: the encoder is
    /// untouched by a capture restart, but the viewer has a gap in its stream
    /// and nothing else will tell it so.
    var onRestarted: ((String) -> Void)?

    /// The capture size changed, which means the display did. The caller has to
    /// rebuild the encoder and send a fresh `video_config` — a viewer laying out
    /// against the old size shows a stretched picture and maps every click to
    /// the wrong place.
    var onSizeChanged: ((H264VideoDimensions) -> Void)?

    /// Restarting cannot recover from this one. Currently only a revoked TCC
    /// grant; everything else is retried forever.
    var onFatalError: ((ScreenCaptureError) -> Void)?

    private let configuration: ScreenCaptureConfiguration
    private let restartPolicy: CaptureRestartPolicy

    /// SCK's sample delivery queue. Nothing of ours is ever scheduled onto it.
    private let frameQueue = DispatchQueue(
        label: "com.dave.lanmessenger.capture.frames", qos: .userInteractive)

    /// Start, stop and restart. Separate from `frameQueue` for the same reason
    /// `MediaSession` keeps three contexts and `DiscoveryService` keeps a
    /// `healthQueue`: the restart path has to run while the delivery path is
    /// wedged, which is precisely when it is needed. It also blocks on stream
    /// teardown, which must never happen on a queue SCK is delivering to.
    private let controlQueue = DispatchQueue(
        label: "com.dave.lanmessenger.capture.control", qos: .userInitiated)

    private let lock = NSLock()
    private var stream: SCStream?
    private var running = false
    private var restartAttempt = 0
    private var size: H264VideoDimensions?
    private var screenParametersObserver: NSObjectProtocol?

    init(configuration: ScreenCaptureConfiguration = ScreenCaptureConfiguration(),
         restartPolicy: CaptureRestartPolicy = CaptureRestartPolicy()) {
        self.configuration = configuration
        self.restartPolicy = restartPolicy
        super.init()
    }

    deinit {
        if let screenParametersObserver {
            NotificationCenter.default.removeObserver(screenParametersObserver)
        }
    }

    /// The current capture size, once a stream has been built.
    var currentSize: H264VideoDimensions? {
        lock.lock(); defer { lock.unlock() }
        return size
    }

    var isRunning: Bool {
        lock.lock(); defer { lock.unlock() }
        return running
    }

    // Small synchronous accessors rather than locking inline. `NSLock` is
    // unavailable from an async context — it is a warning today and an error in
    // Swift 6 — because a suspension while holding it would block a cooperative
    // thread for as long as the continuation takes to resume.
    private func setRunning(_ value: Bool) {
        lock.lock(); running = value; lock.unlock()
    }

    /// Installs a freshly started stream and reports whether the picture size
    /// changed with it.
    private func adopt(stream newStream: SCStream, size newSize: H264VideoDimensions) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let changed = size != newSize
        stream = newStream
        size = newSize
        return changed
    }

    // MARK: - Lifecycle

    /// Builds and starts the stream.
    ///
    /// Preflights the TCC grant and throws rather than requesting it. Requesting
    /// belongs to the consent flow, where a human is already being asked
    /// something and a second system prompt makes sense; raising one from here
    /// would mean an incoming invite could pop a permission dialog on a host
    /// that has not agreed to anything yet.
    func start() async throws {
        guard CGPreflightScreenCaptureAccess() else {
            NetLogger.remote(event: "capture_permission_denied")
            throw ScreenCaptureError.permissionDenied
        }

        setRunning(true)
        try await buildStream()
        await installScreenParametersObserver()
    }

    /// Idempotent. Safe to call from any queue except `frameQueue`.
    func stop() {
        lock.lock()
        running = false
        let current = stream
        stream = nil
        restartAttempt = 0
        lock.unlock()

        if let observer = screenParametersObserver {
            NotificationCenter.default.removeObserver(observer)
            screenParametersObserver = nil
        }
        guard let current else { return }
        controlQueue.async { Self.tearDown(current) }
    }

    private func buildStream() async throws {
        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(
                false, onScreenWindowsOnly: false)
        } catch {
            // SCK reports a revoked grant as an opaque stream error. Re-checking
            // the gate is the only way to tell "permission went away" from
            // "the window server is busy", and only the first is fatal.
            if !CGPreflightScreenCaptureAccess() { throw ScreenCaptureError.permissionDenied }
            throw ScreenCaptureError.streamFailed(error.localizedDescription)
        }

        let display: SCDisplay
        if let wanted = configuration.displayID {
            guard let found = content.displays.first(where: { $0.displayID == wanted }) else {
                throw ScreenCaptureError.noDisplay(wanted)
            }
            display = found
        } else {
            guard let first = content.displays.first else {
                throw ScreenCaptureError.noDisplay(nil)
            }
            display = first
        }

        let scale = await ScreenCaptureGeometry.backingScale(for: display.displayID)
        let pixels = ScreenCaptureGeometry.pixelSize(
            pointWidth: display.width, pointHeight: display.height,
            backingScale: scale, maxLongEdge: configuration.maxLongEdge)
        let resolved = H264VideoDimensions(width: pixels.width, height: pixels.height)

        let streamConfiguration = Self.makeStreamConfiguration(
            configuration, width: pixels.width, height: pixels.height)
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let stream = SCStream(filter: filter, configuration: streamConfiguration, delegate: self)

        do {
            try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: frameQueue)
            try await stream.startCapture()
        } catch {
            throw ScreenCaptureError.streamFailed(error.localizedDescription)
        }

        let sizeChanged = adopt(stream: stream, size: resolved)

        NetLogger.remote(event: "capture_started",
                         reason: "\(resolved.width)x\(resolved.height)@\(configuration.frameRate)")
        if sizeChanged { onSizeChanged?(resolved) }
    }

    /// Maps intent onto `SCStreamConfiguration`. Pure and static so the mapping
    /// is testable on a machine with no capture grant, which is most of them.
    static func makeStreamConfiguration(
        _ configuration: ScreenCaptureConfiguration,
        width: Int,
        height: Int
    ) -> SCStreamConfiguration {
        let stream = SCStreamConfiguration()
        stream.width = width
        stream.height = height

        // 420v, video range, BT.709 — the same choices `H264Encoder` makes, and
        // they have to agree. Capturing full-range pixels into a video-range
        // encoder is how blacks come out washed out and whites clipped on the
        // far side, and nothing in either component complains.
        stream.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        stream.colorMatrix = kCVImageBufferYCbCrMatrix_ITU_R_709_2
        stream.colorSpaceName = CGColorSpace.itur_709

        // A ceiling, not a rate. SCK delivers on change and nothing forces a
        // frame when the screen is still.
        stream.minimumFrameInterval = CMTime(
            value: 1, timescale: CMTimeScale(max(configuration.frameRate, 1)))
        stream.queueDepth = max(2, configuration.queueDepth)
        stream.showsCursor = configuration.showsCursor
        stream.scalesToFit = true
        return stream
    }

    // MARK: - Frames

    func stream(_ stream: SCStream,
                didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
                of outputType: SCStreamOutputType) {
        guard outputType == .screen else { return }
        guard let frame = Self.deliverableFrame(from: sampleBuffer) else { return }

        // A frame arriving means the stream is healthy, whatever it did before.
        lock.lock(); restartAttempt = 0; lock.unlock()

        onFrame?(frame.pixelBuffer, frame.captureUs)
    }

    /// Extracts a picture and its capture time, or nil if this sample is not one.
    ///
    /// SCK marks idle, blank and suspended frames in the sample attachments and
    /// still delivers them, carrying whatever pixels were in the pool. Encoding
    /// one emits a perfectly valid picture of the wrong thing — a stale frame,
    /// or a black one, with no error anywhere.
    static func deliverableFrame(
        from sampleBuffer: CMSampleBuffer
    ) -> (pixelBuffer: CVPixelBuffer, captureUs: UInt64)? {
        guard CMSampleBufferIsValid(sampleBuffer) else { return nil }

        // Only an *explicit* non-complete status drops the frame. SCK always
        // sets it today, but treating a missing status as a drop would mean a
        // future SCK that stopped setting it produced no video at all, silently
        // — and "no picture, no error" is the worst failure this feature has.
        // Encoding the occasional idle frame is a visible artefact for one
        // frame; encoding nothing is indistinguishable from a crash.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(
            sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
           let raw = attachments.first?[.status] as? Int,
           SCFrameStatus(rawValue: raw) != .complete {
            return nil
        }

        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return nil }
        return (pixelBuffer, captureMicroseconds(of: sampleBuffer))
    }

    /// The host clock, in microseconds, carried verbatim into the media frame
    /// header. Derived from the sample's own presentation time rather than from
    /// `Date()` at delivery: the difference is the time the frame spent in SCK's
    /// pool, which is exactly the latency the measurement exists to catch.
    static func captureMicroseconds(of sampleBuffer: CMSampleBuffer) -> UInt64 {
        let presentation = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let source = presentation.isValid && presentation.value > 0
            ? presentation
            : CMClockGetTime(CMClockGetHostTimeClock())
        let micros = CMTimeConvertScale(source, timescale: 1_000_000,
                                        method: .roundHalfAwayFromZero)
        return micros.value > 0 ? UInt64(micros.value) : 0
    }

    // MARK: - Restart

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        NetLogger.remote(event: "capture_stopped", reason: error.localizedDescription)
        scheduleRestart(reason: error.localizedDescription)
    }

    /// Restarts after backoff, on `controlQueue`.
    ///
    /// The attempt counter is reset by the first delivered frame rather than by
    /// a successful `startCapture`: a stream that starts and then immediately
    /// stops again would otherwise retry at 250 ms forever.
    private func scheduleRestart(reason: String) {
        lock.lock()
        guard running else { lock.unlock(); return }
        let attempt = restartAttempt
        restartAttempt += 1
        let dying = stream
        stream = nil
        lock.unlock()

        let delay = restartPolicy.delay(forAttempt: attempt)
        controlQueue.async { [weak self] in
            if let dying { Self.tearDown(dying) }
            guard let self else { return }
            self.controlQueue.asyncAfter(deadline: .now() + delay) {
                Task { await self.restart(reason: reason, attempt: attempt) }
            }
        }
    }

    private func restart(reason: String, attempt: Int) async {
        guard isRunning else { return }
        do {
            try await buildStream()
            NetLogger.remote(event: "capture_restarted", reason: "\(reason) (attempt \(attempt + 1))")
            onRestarted?(reason)
        } catch ScreenCaptureError.permissionDenied {
            // The only failure retrying cannot fix.
            setRunning(false)
            NetLogger.remote(event: "capture_permission_revoked")
            onFatalError?(.permissionDenied)
        } catch {
            scheduleRestart(reason: "\(error)")
        }
    }

    /// Re-resolves geometry when the display configuration changes.
    ///
    /// A resolution change does not always stop the stream — sometimes SCK keeps
    /// running and quietly scales the new desktop into the old frame size, which
    /// looks like a soft picture and mis-maps every injected click. So the
    /// notification is watched as well as `didStopWithError`.
    @MainActor
    private func installScreenParametersObserver() {
        guard screenParametersObserver == nil else { return }
        screenParametersObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self, self.isRunning else { return }
            NetLogger.remote(event: "capture_screen_parameters_changed")
            self.scheduleRestart(reason: "screen_parameters_changed")
        }
    }

    /// Teardown, with a timeout, on a queue that is not SCK's.
    ///
    /// The completion-handler form, because the async one can hop to the main
    /// actor for its XPC teardown — two concurrent stops beachballed the
    /// screenshot path exactly that way. Unlike the screenshot path this one
    /// *waits*: a long-lived source restarts into a new stream immediately, and
    /// starting one while the old session is still tearing down races SCK badly
    /// enough that the new stream can deliver nothing at all.
    private static func tearDown(_ stream: SCStream, timeout: TimeInterval = 3.0) {
        let finished = DispatchSemaphore(value: 0)
        stream.stopCapture { _ in finished.signal() }
        if finished.wait(timeout: .now() + timeout) == .timedOut {
            NetLogger.remote(event: "capture_stop_timeout", reason: "\(timeout)s")
        }
    }
}
