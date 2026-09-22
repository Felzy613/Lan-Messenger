import Foundation

/// The protocol's "never buffer more than two video frames anywhere", as an
/// object rather than a comment.
///
/// PROTOCOL.md states it once and it has to hold at **five** independent places:
/// the capture queue, the encoder, the socket writer, TCP itself, and the
/// decoder/display. Four of them enforce it with machinery that already exists —
/// SCK's frame pool is sized at the source, `MediaWriteScheduler` keeps one
/// in-progress plus one queued, `SO_SNDBUF` is capped so the kernel cannot hold
/// a second of video, and the presenter drops rather than queues. The encoder
/// had nothing: a submit went in whenever the codec said it had room, and a
/// hardware MFT says it has room for as many frames as its pipeline is deep.
///
/// Every frame held anywhere is latency the viewer can never pay back, because
/// this is live screen content — there is no value in a frame that arrives late,
/// only in the next one. So the budget refuses rather than queues, and the
/// refusal is counted: a budget that is constantly full is a real signal about
/// the encoder, and silently dropping would hide it.
///
/// **A codec's own pipeline is not a queue.** This was very nearly shipped with
/// the encoder capped at two, which is correct for a queue and wrong for a
/// hardware transform: Quick Sync issues a `METransformNeedInput` for every slot
/// in its pipeline and does not emit its first frame until enough of them are
/// filled. Capped at two it produced **no output at all** — no video, and not
/// one `encoder_stats` line to say why. A pipeline's depth is fixed latency, not
/// growth, so the encoder gets a *runaway* guard at `pipelineCapacity` and
/// reports its high-water mark; the places that really do queue keep the
/// protocol's two.
///
/// Mirror of `VideoFrameBudget.cs`.
final class VideoFrameBudget {

    /// Fixed by the protocol, not tunable. One being worked on, one waiting.
    /// This is the number for anything that *queues*.
    static let protocolCapacity = 2

    /// For a codec pipeline, which is deep by design. High enough not to starve
    /// a hardware transform, low enough that a runaway is caught long before it
    /// becomes seconds of latency.
    static let pipelineCapacity = 8

    let capacity: Int

    init(capacity: Int = VideoFrameBudget.protocolCapacity) {
        self.capacity = capacity
    }

    private let lock = NSLock()
    private var inFlightCount = 0
    private var refusedCount = 0
    private var highWaterCount = 0

    var inFlight: Int { lock.lock(); defer { lock.unlock() }; return inFlightCount }
    var refused: Int { lock.lock(); defer { lock.unlock() }; return refusedCount }
    /// The deepest this ever got. The encoder's is its pipeline depth, which is
    /// latency nothing else can see — and the number WS10 exists to expose.
    var highWater: Int { lock.lock(); defer { lock.unlock() }; return highWaterCount }

    /// Takes a slot, or refuses. A refusal means drop this frame — never wait
    /// for one, because the capture thread is the thread that would be waiting
    /// and the screen does not stop changing while it does.
    func tryAcquire() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard inFlightCount < capacity else {
            refusedCount += 1
            return false
        }
        inFlightCount += 1
        highWaterCount = max(highWaterCount, inFlightCount)
        return true
    }

    /// Gives a slot back, when a frame comes out or its submission failed.
    ///
    /// Clamped at zero rather than trusted: an encoder that emits two outputs
    /// for one input would otherwise drive the count negative and the budget
    /// would stop bounding anything at all — the failure would be a slow drift
    /// back into unbounded latency, with nothing to see.
    func release() {
        lock.lock(); defer { lock.unlock() }
        if inFlightCount > 0 { inFlightCount -= 1 }
    }

    /// Forgets everything in flight. For flush, restart and teardown, where the
    /// frames that were outstanding are never coming back and a leaked slot
    /// would shrink the budget for the rest of the session.
    func reset() {
        lock.lock(); defer { lock.unlock() }
        inFlightCount = 0
    }

    /// For the stats line. Refusals are not reset with the count.
    var summary: String {
        "in_flight=\(inFlight)/\(capacity) peak=\(highWater) refused=\(refused)"
    }
}
