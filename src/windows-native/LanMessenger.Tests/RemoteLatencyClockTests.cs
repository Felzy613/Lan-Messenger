using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

/// <summary>
/// The stats line's one piece of arithmetic that cannot be checked by looking
/// at it, because whether it is right depends on which machine stamped the
/// frame.
/// </summary>
[TestClass]
public class RemoteLatencyClockTests
{
    private const long Ms = 1000;               // microseconds in a millisecond

    [TestMethod]
    public void ASharedClockIsReportedUntouched()
    {
        // Self-view: host and viewer are the same process, so the subtraction
        // already is capture-to-glass and must not be adjusted away. This is
        // the measurement the whole WS10 performance pass was run against.
        var clock = new RemoteLatencyClock();

        Assert.AreEqual(45 * Ms, clock.Adjust(45 * Ms));
        Assert.AreEqual(38 * Ms, clock.Adjust(38 * Ms));
        Assert.AreEqual(61 * Ms, clock.Adjust(61 * Ms));
        Assert.IsTrue(clock.ClocksAreShared);
        Assert.AreEqual("shared", clock.Label);
    }

    [TestMethod]
    public void TwoMachinesReportDelayAboveTheBestFrameRatherThanAnEpochGap()
    {
        // The bug this exists for. A real session between the Mac and the Dell
        // reported latency_ms_avg=2813817527 — about 32.5 days — while the
        // window was visibly keeping up at 29fps. That is the distance between
        // the two machines' Stopwatch origins, and it swamps the latency
        // completely.
        const long epoch = 2_813_817_000L * Ms;

        var clock = new RemoteLatencyClock();
        long first = clock.Adjust(epoch + 40 * Ms);

        Assert.IsFalse(clock.ClocksAreShared, "a 32-day gap is not a latency");
        Assert.AreEqual("rel", clock.Label);
        Assert.AreEqual(0, first, "the first frame is the best one so far");

        // A frame 25ms worse than the best is reported as 25ms, which is the
        // queueing delay and the part that actually varies.
        Assert.AreEqual(25 * Ms, clock.Adjust(epoch + 65 * Ms));
        Assert.AreEqual(2 * Ms, clock.Adjust(epoch + 42 * Ms));
    }

    [TestMethod]
    public void ABetterFrameMovesTheFloorDown()
    {
        // The floor is the best frame of the session, not the first one — an
        // unlucky first frame would otherwise make every later one look fast.
        const long epoch = 500_000L * Ms;
        var clock = new RemoteLatencyClock();

        clock.Adjust(epoch + 90 * Ms);                       // unlucky first
        Assert.AreEqual(0, clock.Adjust(epoch + 30 * Ms));   // the real floor
        Assert.AreEqual(20 * Ms, clock.Adjust(epoch + 50 * Ms));
        Assert.AreEqual(epoch + 30 * Ms, clock.FloorUs);
    }

    [TestMethod]
    public void ABackwardsStampDoesNotBecomeAHugeLatency()
    {
        // The viewer's clock can be behind the host's, which makes the raw
        // difference negative. Treated as an epoch mismatch it is handled the
        // same way as a positive one; what must never happen is a negative
        // floor being *added* to later samples.
        var clock = new RemoteLatencyClock();

        Assert.AreEqual(0, clock.Adjust(-60_000_000L));
        Assert.IsFalse(clock.ClocksAreShared);
        Assert.AreEqual(10 * Ms, clock.Adjust(-60_000_000L + 10 * Ms));
    }

    [TestMethod]
    public void TheSharedVerdictIsNotRevisedByALaterSlowFrame()
    {
        // Once the floor is small the clocks are the same clock, and a single
        // frame that took two seconds must not flip the whole session into
        // relative mode — the reading would silently change meaning mid-log.
        var clock = new RemoteLatencyClock();
        clock.Adjust(30 * Ms);

        Assert.AreEqual(2_000 * Ms, clock.Adjust(2_000 * Ms));
        Assert.IsTrue(clock.ClocksAreShared);
    }

    [TestMethod]
    public void ResetForgetsTheFloorForTheNextSession()
    {
        // The next session may be against a different machine, so carrying the
        // floor over would subtract one peer's epoch from another's stamps.
        var clock = new RemoteLatencyClock();
        clock.Adjust(2_813_817_000L * Ms);
        Assert.IsFalse(clock.ClocksAreShared);

        clock.Reset();
        Assert.IsNull(clock.FloorUs);
        Assert.AreEqual(40 * Ms, clock.Adjust(40 * Ms));
        Assert.IsTrue(clock.ClocksAreShared);
    }
}
