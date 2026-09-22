using System;

namespace LanMessenger.Core.Networking.Media;

/// <summary>
/// Estimates how far the peer's media clock is from ours. Mirror of
/// <c>RemoteClockSync.swift</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>capture_us</c> rides along with every video frame, and subtracting it from
/// our own clock is the whole latency measurement — but it is stamped on the
/// <b>peer's</b> clock, and both clocks are monotonic-since-boot with unrelated
/// origins. Between two real machines the difference is dominated by the gap
/// between two boot times: the first cross-machine session reported an average
/// latency of 32.5 days while visibly keeping up at 29fps.
/// </para>
/// <para>
/// So the gap is measured rather than assumed away, by the oldest trick there
/// is. We send <c>ping</c> noting our clock, the peer answers <c>pong</c> noting
/// theirs, and we note our clock again on arrival:
/// </para>
/// <code>
///   t1  our clock when the ping left
///   t3  their clock when the pong left       (carried in the pong)
///   t4  our clock when the pong arrived
/// </code>
/// <para>
/// Assuming the two legs took the same time — which is the assumption, and the
/// reason the error is bounded by half the round trip — their clock read
/// <c>t3</c> at our time <c>(t1 + t4) / 2</c>, so
/// </para>
/// <code>
///   offset = t3 - (t1 + t4) / 2       their clock minus ours
///   rtt    = t4 - t1                  measured entirely on our own clock
/// </code>
/// <para>
/// <b>The best sample wins, not the newest.</b> A sample's error is bounded by
/// half its round trip, so the one with the lowest RTT is the most trustworthy
/// estimate we have ever taken; averaging drags it back towards the samples that
/// sat in a queue. This is what NTP does and for the same reason.
/// </para>
/// <para>
/// There is no ping missing from the protocol: <c>ping</c> and <c>pong</c> have
/// been in PROTOCOL.md and both control codecs since WS1, described as
/// "keepalive and round-trip measurement". Nothing sent them until now.
/// </para>
/// </remarks>
public sealed class RemoteClockSync
{
    /// <summary>
    /// A round trip longer than this is not a measurement, it is a stall — the
    /// symmetry assumption cannot survive it and the resulting offset would be
    /// wrong by up to a second.
    /// </summary>
    public const ulong MaxUsableRttUs = 2_000_000;

    private readonly object _gate = new();
    private long? _offsetUs;
    private ulong? _rttUs;
    private int _samples;
    private int _rejected;

    public long? OffsetUs { get { lock (_gate) return _offsetUs; } }
    public ulong? RttUs { get { lock (_gate) return _rttUs; } }
    public int Samples { get { lock (_gate) return _samples; } }
    public int Rejected { get { lock (_gate) return _rejected; } }

    /// <summary>
    /// True once an offset has been measured and peer timestamps can be
    /// converted rather than guessed at.
    /// </summary>
    public bool IsSynced => OffsetUs is not null;

    /// <summary>
    /// Folds one completed round trip in. Returns true when it became the best
    /// sample so far, which is the only time the estimate changes.
    /// </summary>
    public bool Record(ulong pingSentUs, ulong pongSentUs, ulong pongReceivedUs)
    {
        lock (_gate)
        {
            // A pong that arrives before its ping left is a corrupt or replayed
            // message, not a fast network.
            if (pongReceivedUs < pingSentUs) { _rejected++; return false; }

            ulong rtt = pongReceivedUs - pingSentUs;
            if (rtt > MaxUsableRttUs) { _rejected++; return false; }

            _samples++;
            if (_rttUs is { } best && rtt >= best) return false;

            // Signed and wide: the two clocks can be days apart in either
            // direction, and the midpoint of two ulongs must not be computed by
            // adding them.
            long midpoint = (long)pingSentUs + (long)(rtt / 2);
            _offsetUs = (long)pongSentUs - midpoint;
            _rttUs = rtt;
            return true;
        }
    }

    /// <summary>
    /// Converts a timestamp on the peer's clock to ours. Null until an offset
    /// has been measured — a caller must not silently fall back to the raw
    /// value, because the raw value is off by the whole offset.
    /// </summary>
    public long? ToLocalUs(ulong peerUs)
    {
        lock (_gate)
        {
            if (_offsetUs is not { } offset) return null;
            return (long)peerUs - offset;
        }
    }

    /// <summary>
    /// One line for the log, in milliseconds because that is the unit the rest
    /// of the latency reporting uses.
    /// </summary>
    public string Summary()
    {
        lock (_gate)
        {
            if (_offsetUs is not { } offset || _rttUs is not { } rtt)
                return $"unsynced samples={_samples} rejected={_rejected}";
            return $"offset_ms={offset / 1000} rtt_ms={rtt / 1000} "
                 + $"samples={_samples} rejected={_rejected}";
        }
    }
}
