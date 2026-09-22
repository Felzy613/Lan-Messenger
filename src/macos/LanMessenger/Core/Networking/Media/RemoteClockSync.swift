import Foundation

/// Estimates how far the peer's media clock is from ours.
///
/// `capture_us` rides along with every video frame, and subtracting it from our
/// own clock is the whole latency measurement — but it is stamped on the
/// **peer's** clock, and both clocks are monotonic-since-boot with unrelated
/// origins. Between two real machines the difference is dominated by the gap
/// between two boot times: the first cross-machine session reported an average
/// latency of 32.5 days while visibly keeping up at 29fps.
///
/// So the gap is measured rather than assumed away, by the oldest trick there
/// is. We send `ping` noting our clock, the peer answers `pong` noting theirs,
/// and we note our clock again on arrival:
///
/// ```text
///   t1  our clock when the ping left
///   t3  their clock when the pong left       (carried in the pong)
///   t4  our clock when the pong arrived
/// ```
///
/// Assuming the two legs took the same time — which is the assumption, and the
/// reason the error is bounded by half the round trip — their clock read `t3` at
/// our time `(t1 + t4) / 2`, so
///
/// ```text
///   offset = t3 - (t1 + t4) / 2       their clock minus ours
///   rtt    = t4 - t1                  measured entirely on our own clock
/// ```
///
/// **The best sample wins, not the newest.** A sample's error is bounded by half
/// its round trip, so the one with the lowest RTT is the most trustworthy
/// estimate we have ever taken; averaging drags it back towards the samples that
/// sat in a queue. This is what NTP does and for the same reason.
///
/// There is no ping missing from the protocol: `ping` and `pong` have been in
/// `PROTOCOL.md` and both control codecs since WS1, described as "keepalive and
/// round-trip measurement". Nothing sent them until now.
struct RemoteClockSync: Equatable {

    /// A round trip longer than this is not a measurement, it is a stall — the
    /// symmetry assumption cannot survive it and the resulting offset would be
    /// wrong by up to a second.
    static let maxUsableRttUs: UInt64 = 2_000_000

    private(set) var offsetUs: Int64?
    private(set) var rttUs: UInt64?
    private(set) var samples = 0
    private(set) var rejected = 0

    /// True once an offset has been measured and peer timestamps can be
    /// converted rather than guessed at.
    var isSynced: Bool { offsetUs != nil }

    /// Folds one completed round trip in. Returns true when it became the best
    /// sample so far, which is the only time the estimate changes.
    @discardableResult
    mutating func record(pingSentUs t1: UInt64,
                         pongSentUs t3: UInt64,
                         pongReceivedUs t4: UInt64) -> Bool {
        // A pong that arrives before its ping left is a corrupt or replayed
        // message, not a fast network.
        guard t4 >= t1 else {
            rejected += 1
            return false
        }
        let rtt = t4 - t1
        guard rtt <= Self.maxUsableRttUs else {
            rejected += 1
            return false
        }

        samples += 1
        if let best = rttUs, rtt >= best { return false }

        // Signed and wide: the two clocks can be days apart in either
        // direction, and the midpoint of two UInt64s must not be computed by
        // adding them.
        let midpoint = Int64(t1) + Int64(rtt / 2)
        offsetUs = Int64(t3) - midpoint
        rttUs = rtt
        return true
    }

    /// Converts a timestamp on the peer's clock to ours. Returns nil until an
    /// offset has been measured — a caller must not silently fall back to the
    /// raw value, because the raw value is off by the whole offset.
    func toLocalUs(peerUs: UInt64) -> Int64? {
        guard let offsetUs else { return nil }
        return Int64(peerUs) - offsetUs
    }

    /// One line for the log. Said in milliseconds because that is the unit the
    /// rest of the latency reporting uses.
    var summary: String {
        guard let offsetUs, let rttUs else {
            return "unsynced samples=\(samples) rejected=\(rejected)"
        }
        return "offset_ms=\(offsetUs / 1000) rtt_ms=\(rttUs / 1000) "
            + "samples=\(samples) rejected=\(rejected)"
    }
}
