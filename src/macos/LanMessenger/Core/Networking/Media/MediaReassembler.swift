import Foundation

// The demux: per-channel fragment buffers, so an interleaved control or input
// frame arriving between two video segments never disturbs the partially
// assembled video frame. That per-channel keying is the reader-side half of the
// writer's interleaving design — without it, interleaving would corrupt video.

/// A fully reassembled inbound frame.
struct MediaInboundFrame: Equatable {
    let channel: MediaChannel
    let flags: MediaFlags
    /// Sequence of the FINAL fragment.
    let sequence: UInt64
    /// Capture timestamp from the FIRST fragment — the whole frame was captured
    /// at one instant, and later fragments carry the same value only by
    /// convention, so the first is the authoritative one.
    let captureUs: UInt64
    let payload: Data
    let fragmentCount: Int
}

final class MediaReassembler {

    enum Outcome: Equatable {
        case complete(MediaInboundFrame)
        case partial
        case fault(MediaProtocolError)
    }

    private struct Pending {
        var payload: Data
        var captureUs: UInt64
        var flags: MediaFlags
        var fragmentCount: Int
    }

    private var pending: [UInt8: Pending] = [:]
    private let maxAssembledBytes: Int
    private let maxFragments: Int

    /// Two caps, not one, and they close different holes.
    ///
    /// The 4 MiB wire cap bounds a single *frame*; it says nothing about the
    /// reassembled total, so the byte cap is needed to bound a long fragment
    /// chain. But a byte cap alone still lets a stream of zero-length fragments
    /// grow a per-channel buffer forever while every individual frame passes the
    /// wire check — a real allocation vector — so the fragment count is capped
    /// independently.
    init(maxAssembledBytes: Int = MediaFrameCodec.maxFrameLength, maxFragments: Int = 512) {
        self.maxAssembledBytes = maxAssembledBytes
        self.maxFragments = maxFragments
    }

    func accept(header: MediaFrameHeader, plaintext: Data) -> Outcome {
        guard let channel = header.channel else {
            // Callers filter unknown channels before here; treat defensively.
            return .partial
        }
        let key = header.rawChannel

        // Not fragmented: a whole frame in one go.
        guard header.isFragmented else {
            if pending[key] != nil {
                // The writer never does this, so it is corruption or an attack.
                return .fault(.wholeFrameMidReassembly(channel))
            }
            return .complete(MediaInboundFrame(
                channel: channel, flags: header.flags, sequence: header.sequence,
                captureUs: header.captureUs, payload: plaintext, fragmentCount: 1))
        }

        var entry = pending[key] ?? Pending(
            payload: Data(), captureUs: header.captureUs, flags: header.flags, fragmentCount: 0)

        guard entry.fragmentCount + 1 <= maxFragments else {
            pending[key] = nil
            return .fault(.fragmentCountExceeded(channel, count: entry.fragmentCount + 1))
        }
        guard entry.payload.count + plaintext.count <= maxAssembledBytes else {
            pending[key] = nil
            return .fault(.reassemblyOverflow(channel, bytes: entry.payload.count + plaintext.count))
        }

        entry.payload.append(plaintext)
        entry.fragmentCount += 1

        guard header.isFinalFragment else {
            pending[key] = entry
            return .partial
        }

        pending[key] = nil
        return .complete(MediaInboundFrame(
            channel: channel,
            flags: entry.flags,          // first fragment's flags, incl. keyframe
            sequence: header.sequence,   // final fragment's sequence
            captureUs: entry.captureUs,  // first fragment's timestamp
            payload: entry.payload,
            fragmentCount: entry.fragmentCount))
    }

    /// Bytes currently held for a channel. Tests and the stats summary only.
    func pendingBytes(for channel: MediaChannel) -> Int {
        pending[channel.rawValue]?.payload.count ?? 0
    }

    func reset() { pending.removeAll() }
}

// MARK: - Inbound sequence enforcement

/// Strictly-increasing sequence enforcement.
///
/// `lastAccepted` starts nil rather than 0, and that choice is load-bearing.
/// The protocol says a reconnect restarts sequencing from zero against a key
/// that has never been used, so the first frame of a session legitimately
/// carries sequence 0. Initialising to 0 and requiring `> last` would reject it,
/// and the session would die at `hello` with a sequence violation that looks
/// exactly like a crypto fault — a miserable thing to diagnose across two
/// platforms. The nil sentinel accepts the first frame whatever its value and
/// enforces strict increase from then on.
struct MediaSequenceGate: Equatable {
    private(set) var lastAccepted: UInt64?

    init() {}

    /// Returns false if the frame must be rejected (and the session closed).
    mutating func admit(_ sequence: UInt64) -> Bool {
        if let last = lastAccepted, sequence <= last { return false }
        lastAccepted = sequence
        return true
    }
}
