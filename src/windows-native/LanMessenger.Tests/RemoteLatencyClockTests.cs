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
        // When the host and viewer clocks are the same clock, the subtraction
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
    public void AMeasuredOffsetRetiresTheFloorHeuristicEntirely()
    {
        // The floor can only ever report delay ABOVE the best frame. Once
        // `RemoteClockSync` has measured the real gap between the two clocks,
        // the true capture-to-glass figure is available and the approximation
        // must not be preferred to it — including for the frame that set the
        // floor, which the floor reports as 0ms and is not.
        const long epoch = 2_813_817_000L * Ms;
        var clock = new RemoteLatencyClock();

        Assert.AreEqual(0, clock.Adjust(epoch + 40 * Ms), "unsynced, this is all we have");
        Assert.AreEqual("rel", clock.Label);

        clock.PeerOffsetUs = -epoch;          // their clock is `epoch` ahead of ours
        Assert.AreEqual(40 * Ms, clock.Adjust(epoch + 40 * Ms));
        Assert.AreEqual(65 * Ms, clock.Adjust(epoch + 65 * Ms));
        Assert.AreEqual("synced", clock.Label);
    }

    [TestMethod]
    public void TheLabelSaysWhichOfThreeDifferentThingsWasMeasured()
    {
        // The three are not comparable and somebody will compare them, so the
        // reading states which one it is rather than leaving it to be inferred.
        var unsynced = new RemoteLatencyClock();
        unsynced.Adjust(2_813_817_000L * Ms);
        Assert.AreEqual("rel", unsynced.Label);

        var sameClock = new RemoteLatencyClock();
        sameClock.Adjust(40 * Ms);
        Assert.AreEqual("shared", sameClock.Label);

        var synced = new RemoteLatencyClock();
        synced.Adjust(2_813_817_000L * Ms);
        synced.PeerOffsetUs = -2_813_817_000L * Ms;
        Assert.AreEqual("synced", synced.Label);
    }

    [TestMethod]
    public void ResetForgetsTheOffsetTooBecauseTheNextPeerHasADifferentOne()
    {
        var clock = new RemoteLatencyClock();
        clock.PeerOffsetUs = 5_000 * Ms;
        Assert.AreEqual("synced", clock.Label);

        clock.Reset();
        Assert.IsNull(clock.PeerOffsetUs);
        Assert.AreEqual(40 * Ms, clock.Adjust(40 * Ms), "and the floor is forgotten as well");
        Assert.AreEqual("shared", clock.Label);
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
