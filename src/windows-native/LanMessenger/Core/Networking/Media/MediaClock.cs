using System.Diagnostics;

namespace LanMessenger.Core.Networking.Media;

/// <summary>
/// The one clock this side stamps and reads media timestamps with.
/// </summary>
/// <remarks>
/// It must be <b>the same clock `capture_us` comes from</b>, or every
/// cross-machine measurement built on it is nonsense. On Windows that is QPC:
/// <see cref="Stopwatch.GetTimestamp"/> and Desktop Duplication's
/// <c>LastPresentTime</c> are the same counter, which is why a frame's stamp can
/// be compared with a reading taken here.
///
/// It is monotonic since boot and shared with nothing on the other machine,
/// which is exactly why the two readings are unrelated: the difference between
/// them is mostly the difference between two boot times.
/// <see cref="RemoteClockSync"/> measures that difference rather than assuming
/// it away.
/// </remarks>
public static class MediaClock
{
    /// <summary>Microseconds on this machine's media timebase.</summary>
    public static ulong NowUs() =>
        (ulong)(Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000L));
}
