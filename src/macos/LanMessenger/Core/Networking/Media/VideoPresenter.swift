import Foundation
import AVFoundation
import AppKit
import CoreMedia

// Display for the viewer half of a remote-desktop session.
//
// The protocol exists so the session can be driven without a window — a test, or
// a headless conformance run, substitutes a recorder — and so the eventual
// Metal presenter, if latency tuning ever needs one, is a swap rather than a
// rewrite.
//
// The AVSampleBufferDisplayLayer implementation looks trivial and has exactly
// one hard part, which is `requiresFlushToResumeDecoding`. When the app is
// occluded, or loses focus, or the decoder hits an error, the layer silently
// drops everything enqueued until `flush()` is called. Nothing throws, the
// status often stays `.rendering`, and the picture simply stops. It is the
// "video froze after I switched apps" bug and it is not a rare case: it will
// happen in the first minute of the first real session.

@MainActor
protocol VideoPresenter: AnyObject {
    /// Called with a decodable sample. Implementations may drop it; they must
    /// ask for a keyframe when they do.
    func present(_ sampleBuffer: CMSampleBuffer)
    /// Drops everything queued and starts again. Callers must follow with a
    /// keyframe — nothing after a flush decodes without one.
    func flush()
    /// Clears the surface entirely, for session end.
    func clear()
    /// Fires when the presenter needs an IDR to make progress. Debounced: once
    /// per stall, not once per frame.
    var onNeedsKeyframe: ((String) -> Void)? { get set }
}

@MainActor
final class SampleBufferVideoPresenter: VideoPresenter {

    /// The layer to host. A viewer window makes a layer-backed view and assigns
    /// this as its layer; nothing in Core knows about the window.
    let layer = AVSampleBufferDisplayLayer()

    var onNeedsKeyframe: ((String) -> Void)?

    /// Counters for the stats channel. Cheap, and the alternative to them is
    /// guessing why a session looked bad.
    private(set) var presentedFrames = 0
    private(set) var flushes = 0
    private(set) var dropsNotReady = 0

    private var keyframeRequestSent = false
    private var activationObserver: NSObjectProtocol?

    init(videoGravity: AVLayerVideoGravity = .resizeAspect) {
        layer.videoGravity = videoGravity
        layer.backgroundColor = NSColor.black.cgColor

        // The second half of the flush story, and the half that matters most.
        // Polling on enqueue cannot help when no frames are arriving — and a
        // remote screen is silent whenever nothing on it moves, because capture
        // is change-driven on both platforms. So the moment the user comes back
        // to the window is also the moment to clear the flag, whether or not the
        // host has anything to send.
        activationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.flushIfRequired(reason: "app_activated") }
        }
    }

    deinit {
        if let activationObserver {
            NotificationCenter.default.removeObserver(activationObserver)
        }
    }

    // MARK: - VideoPresenter

    func present(_ sampleBuffer: CMSampleBuffer) {
        // Checked before every enqueue rather than observed through KVO. The one
        // thing an observer adds is noticing a required flush while the stream is
        // silent, and the activation hook above covers that case — which is also
        // the case that actually occurs, since the flag is set by occlusion.
        flushIfRequired(reason: "requires_flush")

        if layer.status == .failed {
            layer.flush()
            flushes += 1
            requestKeyframe("layer_failed")
        }

        guard layer.isReadyForMoreMediaData else {
            // Dropping is right: this is live screen content, and a queued
            // backlog is latency the viewer can never pay off. Dropping a
            // non-keyframe corrupts everything until the next IDR, so ask for
            // one rather than showing a slowly disintegrating picture.
            dropsNotReady += 1
            requestKeyframe("layer_not_ready")
            return
        }

        if #available(macOS 14.0, *) {
            layer.sampleBufferRenderer.enqueue(sampleBuffer)
        } else {
            layer.enqueue(sampleBuffer)
        }
        presentedFrames += 1
        notePresented(sampleBuffer)

        // The stall is over only once a keyframe has actually gone in. Clearing
        // on any enqueue would re-arm the debounce every frame and turn a
        // persistent failure into one request per frame.
        if H264Decoder.isKeyframe(sampleBuffer) { keyframeRequestSent = false }
    }

    // MARK: - Measurement

    /// How far the host's clock is from ours. Nil until the ping exchange has
    /// produced an estimate, and while it is nil no latency is reported at all
    /// — a figure computed straight off `capture_us` is out by the gap between
    /// two boot times, and a wrong number is worse than none because somebody
    /// will act on it.
    var clockSync: RemoteClockSync? {
        didSet { if clockSync?.isSynced == true, oldValue?.isSynced != true { resetStats() } }
    }

    private var statsStartedAt = Date()
    private var latencySumMs: Int64 = 0
    private var latencySamples: Int64 = 0
    private var latencyMaxMs: Int64 = 0
    private var statsPresented = 0

    /// Glass-to-glass, as far as this side can see it: the frame's capture
    /// stamp converted onto our clock and subtracted from now.
    ///
    /// "As far as this side can see it" is the honest caveat. The enqueue is the
    /// last moment we control; the compositor puts it on the glass a frame or so
    /// later, and the host's own capture-to-encode time is already inside
    /// `capture_us`. What is missing is bounded and known, which is the
    /// difference between a measurement and a guess.
    private func notePresented(_ sampleBuffer: CMSampleBuffer) {
        statsPresented += 1

        if let sync = clockSync, sync.isSynced {
            let stamp = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
            let captureUs = CMTimeConvertScale(stamp, timescale: 1_000_000,
                                               method: .roundHalfAwayFromZero).value
            if captureUs > 0, let local = sync.toLocalUs(peerUs: UInt64(captureUs)) {
                let latencyMs = (Int64(MediaClock.nowUs()) - local) / 1000
                // A slightly-off estimate can put a fast frame marginally
                // negative. Skipped rather than clamped: a zero would look like
                // a real reading.
                if latencyMs >= 0 {
                    latencySumMs += latencyMs
                    latencySamples += 1
                    latencyMaxMs = max(latencyMaxMs, latencyMs)
                }
            }
        }

        let elapsed = Date().timeIntervalSince(statsStartedAt)
        guard elapsed >= 2 else { return }

        let fps = String(format: "%.1f", Double(statsPresented) / elapsed)
        let latency = latencySamples > 0
            ? "clock=synced latency_ms_avg=\(latencySumMs / latencySamples) "
              + "latency_ms_max=\(latencyMaxMs)"
            : "clock=unsynced"
        NetLogger.remote(event: "viewer_stats",
                         reason: "presented=\(statsPresented) drops_not_ready=\(dropsNotReady) "
                               + "flushes=\(flushes) fps=\(fps) \(latency)")
        resetStats()
    }

    private func resetStats() {
        statsStartedAt = Date()
        statsPresented = 0
        latencySumMs = 0
        latencySamples = 0
        latencyMaxMs = 0
    }

    func flush() {
        layer.flush()
        flushes += 1
        requestKeyframe("flush")
    }

    func clear() {
        layer.flushAndRemoveImage()
        flushes += 1
        keyframeRequestSent = false
    }

    // MARK: - Private

    private func flushIfRequired(reason: String) {
        guard layer.requiresFlushToResumeDecoding else { return }
        layer.flush()
        flushes += 1
        NetLogger.remote(event: "presenter_flush", reason: reason)
        requestKeyframe(reason)
    }

    private func requestKeyframe(_ reason: String) {
        guard !keyframeRequestSent else { return }
        keyframeRequestSent = true
        NetLogger.remote(event: "presenter_needs_keyframe", reason: reason)
        onNeedsKeyframe?(reason)
    }
}
