import Foundation
import CoreMedia

// The viewer's video path, from a reassembled media frame to a sample buffer
// ready to display.
//
// Presentation is deliberately *not* here. `VideoPresenter` is main-actor work
// and this runs on the session's read queue; putting the hop inside the pipeline
// would make every consumer main-actor-shaped, including the tests, for no gain.
// The owner wires `onSample` to its presenter and does the hop itself.
//
// The one coupling that must not be lost in that split: **a presenter flush and
// a decoder reset go together.** After the layer is flushed nothing decodes
// until an IDR, and a decoder that does not know it was flushed keeps handing
// over P-frames that are silently discarded — a picture that never returns, with
// no error anywhere. `reset(reason:)` is the other half of `flush()`.

final class VideoReceivePipeline {

    struct Statistics: Equatable {
        var framesAccepted = 0
        var samplesProduced = 0
        var framesDropped = 0
        var keyframeRequests = 0
        var bytesAccepted = 0
    }

    /// Delivered on the caller's thread — the session read queue in production.
    var onSample: ((CMSampleBuffer) -> Void)?

    /// The stream cannot progress without an IDR. The owner sends a
    /// `keyframe_request` on the control channel. Debounced by the decoder:
    /// once per stall, not once per frame.
    var onKeyframeNeeded: ((String) -> Void)?

    /// The picture size, the first time it is known and on every change. The
    /// viewer window resizes and input coordinate mapping follows it.
    var onDimensionsChanged: ((H264VideoDimensions) -> Void)?

    private let decoder = H264Decoder()
    private let lock = NSLock()
    private var statistics = Statistics()

    init() {
        decoder.onNeedsKeyframe = { [weak self] reason in
            guard let self else { return }
            self.lock.lock(); self.statistics.keyframeRequests += 1; self.lock.unlock()
            self.onKeyframeNeeded?(reason)
        }
        decoder.onDimensionsChanged = { [weak self] size in
            self?.onDimensionsChanged?(size)
        }
    }

    var stats: Statistics {
        lock.lock(); defer { lock.unlock() }
        return statistics
    }

    var dimensions: H264VideoDimensions? { decoder.dimensions }

    /// Decodes one reassembled video frame.
    ///
    /// Not thread-safe, and does not need to be: `MediaSession` delivers on a
    /// single read queue. Calling it from two threads would corrupt the
    /// decoder's format-description state, which is exactly the kind of fault
    /// that shows up as an occasional unexplained green frame.
    func accept(_ frame: MediaInboundFrame) {
        guard frame.channel == .video else { return }

        lock.lock()
        statistics.framesAccepted += 1
        statistics.bytesAccepted += frame.payload.count
        lock.unlock()

        do {
            let samples = try decoder.decode(annexB: frame.payload, captureUs: frame.captureUs)
            if samples.isEmpty {
                // Not an error. The decoder drops until it has parameter sets
                // and an IDR, and it has already asked for one.
                lock.lock(); statistics.framesDropped += 1; lock.unlock()
                return
            }
            lock.lock(); statistics.samplesProduced += samples.count; lock.unlock()
            for sample in samples { onSample?(sample) }
        } catch {
            lock.lock(); statistics.framesDropped += 1; lock.unlock()
            NetLogger.remote(event: "decode_failed", channel: Int(frame.channel.rawValue),
                             sequence: frame.sequence, reason: "\(error)")
            // A malformed frame is not fatal — the next IDR repairs the stream.
            decoder.reset(reason: "decode_failed")
        }
    }

    /// The other half of a presenter flush. Everything after one is undecodable
    /// until an IDR, so this also asks for one.
    func reset(reason: String) {
        decoder.reset(reason: reason)
    }
}
