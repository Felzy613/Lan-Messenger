import Foundation
import CoreVideo

// The host's video path, from a captured pixel buffer to a frame on the wire.
//
// Everything it joins already existed and had never been connected: capture
// produced pixel buffers nobody encoded, the encoder produced AVCC nobody
// converted, and the transport moved payloads nobody generated. This is the
// seam between them, and it is deliberately thin — it owns one encoder and one
// rule about when to convert, and nothing else.
//
// Two things happen here rather than in the encoder, because they are wire
// concerns rather than codec ones:
//
//  * **AVCC becomes Annex-B, with the parameter sets in-band at every IDR.**
//    That is what PROTOCOL.md specifies and what Media Foundation needs. The
//    encoder holds its parameter sets in a CMVideoFormatDescription and has no
//    opinion about the wire.
//  * **A keyframe request becomes a per-frame encode option.** VideoToolbox has
//    no "send an IDR now" call — `kVTEncodeFrameOptionKey_ForceKeyFrame` rides
//    the next frame submitted. On a still screen that frame may be a long time
//    coming, which is why a viewer's request is latched rather than applied.

final class VideoSendPipeline {

    /// Counters for the stats sub-channel.
    struct Statistics: Equatable {
        var framesEncoded = 0
        var framesSubmitted = 0
        var keyframesSubmitted = 0
        var framesDropped = 0
        var bytesSubmitted = 0
    }

    /// Raised when the encoder fails in a way a new session would fix. The owner
    /// tears the session down; retrying in place risks a half-configured
    /// compression session producing an unplayable stream forever.
    var onError: ((Error) -> Void)?

    private let submit: (MediaOutboundFrame) -> MediaWriteScheduler.Submission
    private let bitrate: Int
    private let frameRate: Int

    private let lock = NSLock()
    private var encoder: H264Encoder?
    private var dimensions: H264VideoDimensions
    private var keyframePending = true
    private var statistics = Statistics()

    /// - Parameter submit: normally `MediaSession.submit`. Injected so the whole
    ///   pipeline can be driven without a session, and so the verdict — which
    ///   carries the scheduler's drop decisions — is not discarded.
    init(dimensions: H264VideoDimensions,
         bitrate: Int = 10_000_000,
         frameRate: Int = 30,
         submit: @escaping (MediaOutboundFrame) -> MediaWriteScheduler.Submission) throws {
        self.dimensions = dimensions
        self.bitrate = bitrate
        self.frameRate = frameRate
        self.submit = submit
        self.encoder = try makeEncoder(dimensions)
    }

    deinit { encoder?.invalidate() }

    var stats: Statistics {
        lock.lock(); defer { lock.unlock() }
        return statistics
    }

    var currentDimensions: H264VideoDimensions {
        lock.lock(); defer { lock.unlock() }
        return dimensions
    }

    // MARK: - Input

    /// Encodes one captured frame.
    ///
    /// Called from the capture source's delivery queue and must not block it:
    /// the pixel buffer belongs to a pool of two or three, and holding one stalls
    /// the capture that produced it. Encoding is asynchronous inside
    /// VideoToolbox, so this returns immediately and the frame arrives on the
    /// encoder's own callback thread.
    func encode(pixelBuffer: CVPixelBuffer, captureUs: UInt64) {
        lock.lock()
        let encoder = self.encoder
        let force = keyframePending
        keyframePending = false
        lock.unlock()

        guard let encoder else { return }
        do {
            try encoder.encode(pixelBuffer: pixelBuffer, captureUs: captureUs, forceKeyframe: force)
        } catch {
            // Put the request back: the frame that was carrying it never got
            // encoded, and a viewer waiting on an IDR would wait forever.
            if force { latchKeyframe() }
            onError?(error)
        }
    }

    /// Honours a viewer's `keyframe_request`.
    ///
    /// Latched rather than applied, because there is nothing to apply it to
    /// until the next captured frame — and on a still screen, capture is silent.
    /// A viewer that has just flushed its decoder therefore recovers on the next
    /// thing that moves, which is the earliest anything could have helped it.
    func latchKeyframe() {
        lock.lock(); keyframePending = true; lock.unlock()
    }

    /// Rebuilds the encoder for a new picture size.
    ///
    /// The session must send a fresh `video_config` alongside this: a viewer
    /// laying out against the old size shows a stretched picture and maps every
    /// injected click to the wrong place.
    func resize(to newDimensions: H264VideoDimensions) throws {
        lock.lock()
        guard newDimensions != dimensions else { lock.unlock(); return }
        let old = encoder
        encoder = nil
        dimensions = newDimensions
        lock.unlock()

        // Drain before discarding. Frames still inside the old session describe
        // the old picture size, and delivering them after the viewer has been
        // told the new one is a guaranteed decode failure.
        old?.flush()
        old?.invalidate()

        let rebuilt = try makeEncoder(newDimensions)
        lock.lock()
        encoder = rebuilt
        keyframePending = true
        lock.unlock()

        NetLogger.remote(event: "encoder_resized",
                         reason: "\(newDimensions.width)x\(newDimensions.height)")
    }

    func stop() {
        lock.lock()
        let current = encoder
        encoder = nil
        lock.unlock()
        current?.flush()
        current?.invalidate()
    }

    // MARK: - Output

    private func makeEncoder(_ size: H264VideoDimensions) throws -> H264Encoder {
        let encoder = try H264Encoder(configuration: H264EncoderConfiguration(
            width: size.width, height: size.height,
            bitrate: bitrate, expectedFrameRate: frameRate))
        encoder.onEncodedFrame = { [weak self] frame in self?.send(frame) }
        encoder.onError = { [weak self] error in self?.onError?(error) }
        return encoder
    }

    /// Runs on VideoToolbox's callback thread. `MediaSession.submit` is
    /// thread-safe and hands off to its own write queue, so there is no reason
    /// to hop first — and a hop here would put a queue between the encoder and
    /// the socket for no benefit.
    private func send(_ frame: H264EncodedFrame) {
        let annexB: Data
        do {
            annexB = try H264Bitstream.avccToAnnexB(
                frame.data,
                nalLengthSize: frame.nalLengthSize,
                parameterSets: frame.parameterSets)
        } catch {
            onError?(error)
            return
        }

        lock.lock(); statistics.framesEncoded += 1; lock.unlock()

        let verdict = submit(MediaOutboundFrame(
            channel: .video, payload: annexB,
            captureUs: frame.captureUs, keyframe: frame.isKeyframe))

        lock.lock()
        switch verdict {
        case .queued:
            statistics.framesSubmitted += 1
            statistics.bytesSubmitted += annexB.count
            if frame.isKeyframe { statistics.keyframesSubmitted += 1 }
        case .droppedStaleVideo(let wasKeyframe):
            statistics.framesDropped += 1
            // The scheduler drops the queued frame, never the one already being
            // fragmented. If the dropped one was a keyframe the viewer is still
            // waiting on an IDR, so ask the encoder for another.
            if wasKeyframe { keyframePending = true }
        case .overflow:
            statistics.framesDropped += 1
        }
        lock.unlock()
    }
}
