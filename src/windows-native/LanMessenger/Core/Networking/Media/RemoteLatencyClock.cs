namespace LanMessenger.Core.Networking.Media;

/// <summary>
/// Turns a raw <c>now - capture_us</c> into a number that means milliseconds,
/// whichever clock stamped the frame.
/// </summary>
/// <remarks>
/// <para>
/// <c>capture_us</c> is stamped on the HOST's clock and subtracted from ours,
/// which is a latency only when both are the same clock. Between two machines
/// with independent clocks the difference is dominated by the gap between their
/// two <c>Stopwatch</c> origins: the first real cross-machine session reported
/// <c>latency_ms_avg=2813817527</c>, about 32.5 days, in a window that was
/// visibly keeping up at 29fps. A figure that wrong is worse than none, because
/// it is still a number and someone will act on it.
/// </para>
/// <para>
/// So the smallest difference of the session is taken as the zero, and what is
/// reported across machines is delay ABOVE the best frame observed — queueing
/// and jitter, which is the part that varies and the part worth watching. The
/// absolute figure is not recoverable without a clock exchange the protocol
/// does not have, and inventing one would be guessing.
/// </para>
/// <para>
/// When the two clocks turn out to be the same one, the raw value already is
/// the answer and is returned untouched.
/// </para>
/// </remarks>
public sealed class RemoteLatencyClock
{
    /// <summary>Beyond this the two clocks cannot be the same one.</summary>
    /// <remarks>
    /// A frame is never ten seconds old: capture, encode and decode are tens of
    /// milliseconds, and a link that far behind would be dead. A difference
    /// this large is an epoch mismatch, not latency.
    /// </remarks>
    public const long SharedClockLimitUs = 10_000_000;

    private long _floorUs = long.MaxValue;
    private long? _offsetUs;

    /// <summary>
    /// How far the peer's clock is ahead of ours, once the ping exchange has
    /// measured it. Setting it retires the floor heuristic entirely: a measured
    /// offset gives the real figure, where the floor can only ever give delay
    /// above the best frame.
    /// </summary>
    public long? PeerOffsetUs
    {
        get => _offsetUs;
        set => _offsetUs = value;
    }

    /// <summary>The smallest difference seen, or null before the first sample.</summary>
    public long? FloorUs => _floorUs == long.MaxValue ? null : _floorUs;

    /// <summary>Whether the host's clock and ours look like the same clock.</summary>
    public bool ClocksAreShared =>
        _floorUs != long.MaxValue && System.Math.Abs(_floorUs) < SharedClockLimitUs;

    /// <summary>What to report for this sample, in microseconds.</summary>
    public long Adjust(long deltaUs)
    {
        if (deltaUs < _floorUs) _floorUs = deltaUs;

        // `deltaUs` is `ourNow - capture_us`, and `capture_us` is on their
        // clock. Their clock reads `offset` more than ours, so converting the
        // stamp onto ours and subtracting again is the same as adding the
        // offset back on — which is the true latency, not a relative one.
        if (_offsetUs is { } offset) return deltaUs + offset;

        return ClocksAreShared ? deltaUs : deltaUs - _floorUs;
    }

    /// <summary>The word for the stats line: what these milliseconds measure.</summary>
    /// <remarks>
    /// <c>synced</c> is a real capture-to-glass figure against a measured clock
    /// offset. <c>shared</c> is the same thing when the two clocks turn out to
    /// be one and the same. <c>rel</c> is delay above the best frame of the
    /// session, which is all that is knowable before the ping exchange has
    /// produced an estimate. The reading always says which it is, because the
    /// three are not comparable and somebody will otherwise compare them.
    /// </remarks>
    public string Label => _offsetUs is not null ? "synced"
                         : ClocksAreShared ? "shared"
                         : "rel";

    /// <summary>
    /// Forgets the session's floor. Called when a session ends, because the next
    /// one may be against a different machine.
    /// </summary>
    public void Reset()
    {
        _floorUs = long.MaxValue;
        _offsetUs = null;
    }
}
