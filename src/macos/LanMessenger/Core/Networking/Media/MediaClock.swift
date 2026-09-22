import CoreMedia
import Foundation

/// The one clock this side stamps and reads media timestamps with.
///
/// It must be **the same clock `capture_us` comes from**, or every cross-machine
/// measurement built on it is nonsense. On macOS that is the host time clock —
/// `ScreenCaptureSource.captureMicroseconds` derives a frame's stamp from the
/// sample's presentation time, which is on this clock, and falls back to reading
/// it directly.
///
/// It is monotonic since boot and shared by nothing else, which is exactly why
/// the two machines' readings are unrelated: the difference between them is
/// mostly the difference between two boot times. `RemoteClockSync` measures that
/// difference rather than assuming it away.
enum MediaClock {

    /// Microseconds on this machine's media timebase.
    static func nowUs() -> UInt64 {
        let now = CMClockGetTime(CMClockGetHostTimeClock())
        let micros = CMTimeConvertScale(now, timescale: 1_000_000,
                                        method: .roundHalfAwayFromZero)
        return micros.value > 0 ? UInt64(micros.value) : 0
    }
}
