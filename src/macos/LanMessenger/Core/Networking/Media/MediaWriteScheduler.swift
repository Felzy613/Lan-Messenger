import Foundation

// The mux: fragmentation, interleaving, and the never-buffer-two-video-frames
// drop policy — as a pure state machine with no threads, no clock and no I/O.
//
// Keeping this pure is the whole reason the head-of-line-blocking fix is
// testable at all: "an input event never waits behind a keyframe" becomes an
// assertion over a sequence of `nextSegment()` calls rather than a stopwatch.

/// One logical frame handed to the scheduler by a producer.
struct MediaOutboundFrame: Equatable {
    let channel: MediaChannel
    let payload: Data
    let captureUs: UInt64
    let keyframe: Bool

    init(channel: MediaChannel, payload: Data, captureUs: UInt64, keyframe: Bool = false) {
        self.channel = channel
        self.payload = payload
        self.captureUs = captureUs
        self.keyframe = keyframe
    }
}

/// One unit the writer seals and writes: at most `maxFragmentPayload` bytes.
struct MediaSegment: Equatable {
    let channel: MediaChannel
    let flags: MediaFlags
    let captureUs: UInt64
    let payload: Data
}

final class MediaWriteScheduler {

    /// Outcome of a submission. Never discard it — `FileTransferService.cs`
    /// ignoring `TryWrite`'s bool is the precedent not to copy, because a
    /// silently dropped record is indistinguishable from a delivered one.
    enum Submission: Equatable {
        case queued
        case droppedStaleVideo(wasKeyframe: Bool)
        case overflow(MediaChannel)
    }

    private let fragmentSize: Int
    private let videoDepth: Int
    private let channelDepth: Int

    private let lock = NSLock()

    // Video is held in exactly two slots: the one being fragmented and at most
    // one waiting behind it. This is the only place video is ever buffered.
    private var inProgress: MediaOutboundFrame?
    private var inProgressCursor = 0
    private var queuedVideo: MediaOutboundFrame?

    // The four non-video channels, each a bounded FIFO.
    private var control: [MediaOutboundFrame] = []
    private var input:   [MediaOutboundFrame] = []
    private var cursor:  [MediaOutboundFrame] = []
    private var stats:   [MediaOutboundFrame] = []

    private(set) var droppedVideoFrames: Int = 0
    private(set) var droppedKeyframes: Int = 0
    /// Latched when a dropped frame was a keyframe, so the encoder driver can
    /// force an IDR on its next submit. Without this a dropped keyframe leaves
    /// the viewer on a degraded image until the next scheduled one.
    private(set) var keyframeDropPending: Bool = false

    init(fragmentSize: Int = MediaFrameCodec.maxFragmentPayload,
         videoDepth: Int = 2,
         channelDepth: Int = 512) {
        precondition(fragmentSize > 0, "fragment size must be positive")
        precondition(videoDepth == 2, "the protocol fixes the video budget at two frames")
        self.fragmentSize = fragmentSize
        self.videoDepth = videoDepth
        self.channelDepth = channelDepth
    }

    // MARK: - Submit

    /// Thread-safe. Video may displace a staler queued frame; the other channels
    /// report overflow, which the session treats as a fault.
    @discardableResult
    func submit(_ frame: MediaOutboundFrame) -> Submission {
        lock.lock(); defer { lock.unlock() }

        guard frame.channel != .video else { return submitVideoLocked(frame) }

        // A vanished input record is a stuck modifier key on the host, and a
        // vanished control record can be the control_grant itself. So these
        // never drop silently: 512 pending records means the socket is wedged
        // and the session is already dead — say so rather than leak memory.
        switch frame.channel {
        case .control: return append(&control, frame)
        case .input:   return append(&input, frame)
        case .cursor:
            // Cursor is the exception: only the newest position has meaning, so
            // it replaces rather than queues. Faulting a session because 513
            // cursor positions piled up would be absurd.
            cursor = [frame]
            return .queued
        case .stats:   return append(&stats, frame)
        case .video:   fatalError("unreachable")
        }
    }

    private func append(_ queue: inout [MediaOutboundFrame], _ frame: MediaOutboundFrame) -> Submission {
        guard queue.count < channelDepth else { return .overflow(frame.channel) }
        queue.append(frame)
        return .queued
    }

    private func submitVideoLocked(_ frame: MediaOutboundFrame) -> Submission {
        if inProgress == nil {
            inProgress = frame
            inProgressCursor = 0
            return .queued
        }
        guard let staleFrame = queuedVideo else {
            queuedVideo = frame
            return .queued
        }
        // Both slots full. Drop the QUEUED frame, never the in-progress one:
        // once fragment 0 of a frame is on the wire the peer is reassembling it,
        // and abandoning it mid-fragmentation leaves a dangling
        // `fragmented`-without-`final` that desyncs their reassembler for good.
        // The queued frame is also the staler of the two, so dropping it is the
        // right freshness choice as well as the only safe one.
        queuedVideo = frame
        droppedVideoFrames += 1
        if staleFrame.keyframe {
            droppedKeyframes += 1
            keyframeDropPending = true
        }
        return .droppedStaleVideo(wasKeyframe: staleFrame.keyframe)
    }

    /// Consumes the latched keyframe-drop flag.
    func takeKeyframeDropPending() -> Bool {
        lock.lock(); defer { lock.unlock() }
        let pending = keyframeDropPending
        keyframeDropPending = false
        return pending
    }

    // MARK: - Drain

    /// Returns the next unit to seal and write, or nil when idle.
    ///
    /// Priority is control → input → cursor → stats → video, and exactly ONE
    /// video segment is emitted per call. Because the writer loop calls this
    /// afresh after every write, anything submitted while a 500 KiB keyframe is
    /// being fragmented goes out *between* its segments rather than after them.
    ///
    /// The order is deliberate: control carries hello and control_grant, and
    /// arming input behind thirty segments of video is how a grant appears to
    /// hang; input is next because a mouse move stuck behind video is the exact
    /// complaint the protocol calls out.
    func nextSegment() -> MediaSegment? {
        lock.lock(); defer { lock.unlock() }

        for queue in [\MediaWriteScheduler.control, \MediaWriteScheduler.input,
                      \MediaWriteScheduler.cursor,  \MediaWriteScheduler.stats] {
            if !self[keyPath: queue].isEmpty {
                let frame = self[keyPath: queue].removeFirst()
                return MediaSegment(channel: frame.channel, flags: [],
                                    captureUs: frame.captureUs, payload: frame.payload)
            }
        }
        return nextVideoSegmentLocked()
    }

    private func nextVideoSegmentLocked() -> MediaSegment? {
        if inProgress == nil, let next = queuedVideo {
            inProgress = next
            queuedVideo = nil
            inProgressCursor = 0
        }
        guard let frame = inProgress else { return nil }

        let remaining = frame.payload.count - inProgressCursor
        let take = min(fragmentSize, remaining)
        let start = frame.payload.startIndex + inProgressCursor
        let slice = Data(frame.payload[start..<(start + take)])

        let isFirst = inProgressCursor == 0
        inProgressCursor += take
        let isLast = inProgressCursor >= frame.payload.count

        // A frame that fits in one fragment must NOT be marked fragmented: a
        // reassembler that sees fragmented-without-final and then a whole frame
        // on the same channel faults, which is correct behaviour against a
        // writer that mislabels.
        var flags: MediaFlags = []
        if frame.keyframe { flags.insert(.keyframe) }
        if !(isFirst && isLast) {
            flags.insert(.fragmented)
            if isLast { flags.insert(.finalFragment) }
        }

        if isLast {
            inProgress = nil
            inProgressCursor = 0
        }
        return MediaSegment(channel: .video, flags: flags,
                            captureUs: frame.captureUs, payload: slice)
    }

    /// True when nothing is waiting. Used only by tests and the drain loop's
    /// exit condition.
    var isIdle: Bool {
        lock.lock(); defer { lock.unlock() }
        return inProgress == nil && queuedVideo == nil
            && control.isEmpty && input.isEmpty && cursor.isEmpty && stats.isEmpty
    }

    /// Drops everything. Called on session teardown so a wedged peer's backlog
    /// is not held alive by the session object.
    func reset() {
        lock.lock(); defer { lock.unlock() }
        inProgress = nil; queuedVideo = nil; inProgressCursor = 0
        control.removeAll(); input.removeAll(); cursor.removeAll(); stats.removeAll()
    }
}
