namespace LanMessenger.Core.Networking.Media;

/// <summary>
/// Turns a raw <c>now - capture_us</c> into a number that means milliseconds,
/// whichever clock stamped the frame.
/// </summary>
/// <remarks>
/// <para>
/// <c>capture_us</c> is stamped on the HOST's clock and subtracted from ours,
/// which is a latency only when both are the same clock — true in self-view and
/// false across a network. Between two machines the difference is dominated by
/// the gap between their two <c>Stopwatch</c> origins: the first real
/// cross-machine session reported <c>latency_ms_avg=2813817527</c>, about 32.5
/// days, in a window that was visibly keeping up at 29fps. A figure that wrong
/// is worse than none, because it is still a number and someone will act on it.
/// </para>
/// <para>
/// So the smallest difference of the session is taken as the zero, and what is
/// reported across machines is delay ABOVE the best frame observed — queueing
/// and jitter, which is the part that varies and the part worth watching. The
/// absolute figure is not recoverable without a clock exchange the protocol
/// does not have, and inventing one would be guessing.
/// </para>
/// <para>
/// On a shared clock the raw value already is the answer and is returned
/// untouched, so self-view keeps reporting true capture-to-glass latency.
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

    /// <summary>The smallest difference seen, or null before the first sample.</summary>
    public long? FloorUs => _floorUs == long.MaxValue ? null : _floorUs;

    /// <summary>Whether the host's clock and ours look like the same clock.</summary>
    public bool ClocksAreShared =>
        _floorUs != long.MaxValue && System.Math.Abs(_floorUs) < SharedClockLimitUs;

    /// <summary>What to report for this sample, in microseconds.</summary>
    public long Adjust(long deltaUs)
    {
        if (deltaUs < _floorUs) _floorUs = deltaUs;
        return ClocksAreShared ? deltaUs : deltaUs - _floorUs;
    }

    /// <summary>The word for the stats line: what these milliseconds measure.</summary>
    public string Label => ClocksAreShared ? "shared" : "rel";

    /// <summary>
    /// Forgets the session's floor. Called when a session ends, because the next
    /// one may be against a different machine.
    /// </summary>
    public void Reset() => _floorUs = long.MaxValue;
}
