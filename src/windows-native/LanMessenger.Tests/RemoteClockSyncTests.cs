using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

/// <summary>
/// The arithmetic that makes a cross-machine latency figure mean anything.
/// Mirror of RemoteClockSyncTests.swift.
/// </summary>
/// <remarks>
/// It cannot be checked by looking at a running session — a wrong offset
/// produces a plausible-looking number, which is exactly how the 32.5-day
/// reading survived a whole evening of testing. So the cases are built from a
/// known ground truth and the answer is checked against it.
/// </remarks>
[TestClass]
public class RemoteClockSyncTests
{
    /// A round trip against a peer whose clock is `offset` microseconds ahead of
    /// ours, with the two legs taking `oneWay` each.
    private static bool RoundTrip(RemoteClockSync sync, ulong t1, long peerAhead,
                                  ulong oneWay, ulong peerDelay = 0)
    {
        ulong ourTimeAtTheirSend = t1 + oneWay + peerDelay;
        ulong t3 = (ulong)((long)ourTimeAtTheirSend + peerAhead);
        ulong t4 = t1 + oneWay + peerDelay + oneWay;
        return sync.Record(t1, t3, t4);
    }

    [TestMethod]
    public void AnOffsetIsRecoveredFromASymmetricRoundTrip()
    {
        // The ground truth: their clock is one second ahead of ours, each leg
        // takes 5ms. The estimate should be exact.
        var sync = new RemoteClockSync();
        Assert.IsTrue(RoundTrip(sync, 4_000_000, 1_000_000, 5_000));

        Assert.AreEqual(1_000_000L, sync.OffsetUs);
        Assert.AreEqual(10_000UL, sync.RttUs);
        Assert.IsTrue(sync.IsSynced);
    }

    [TestMethod]
    public void APeerBehindUsGivesANegativeOffset()
    {
        // Both directions have to work: whichever machine booted first is an
        // accident, and an unsigned offset would fail on half the pairs.
        var sync = new RemoteClockSync();
        RoundTrip(sync, 9_000_000, -2_500_000, 3_000);

        Assert.AreEqual(-2_500_000L, sync.OffsetUs);
    }

    [TestMethod]
    public void TheFigureThisExistsForComesOutRight()
    {
        // The real reading from the first cross-machine session: a viewer
        // subtracting the host's capture_us from its own clock got about 32.5
        // days. With the offset measured, the same frame reads as 40ms.
        const long peerAhead = 2_813_817_000_000;       // ~32.5 days
        var sync = new RemoteClockSync();
        RoundTrip(sync, 4_000_000, peerAhead, 5_000);

        // A frame captured on their clock, presented 40ms later on ours.
        const ulong ourTimeAtCapture = 4_500_000;
        ulong captureUs = (ulong)((long)ourTimeAtCapture + peerAhead);
        const ulong presentedAt = ourTimeAtCapture + 40_000;

        long? capturedOnOurClock = sync.ToLocalUs(captureUs);
        Assert.IsNotNull(capturedOnOurClock, "a synced estimator refused to convert");
        Assert.AreEqual(40_000L, (long)presentedAt - capturedOnOurClock!.Value);
    }

    [TestMethod]
    public void TheBestSampleWinsRatherThanTheNewest()
    {
        // A sample's error is bounded by half its round trip, so the lowest-RTT
        // sample is the most trustworthy estimate ever taken. Letting a later,
        // slower one overwrite it is how a good measurement gets thrown away.
        var sync = new RemoteClockSync();
        Assert.IsTrue(RoundTrip(sync, 1_000_000, 500_000, 2_000));
        Assert.AreEqual(4_000UL, sync.RttUs);

        // Slower, and asymmetric enough to be wrong: it must not be taken.
        Assert.IsFalse(sync.Record(2_000_000, 2_600_000, 2_400_000));
        Assert.AreEqual(500_000L, sync.OffsetUs, "a slower sample replaced a better one");
        Assert.AreEqual(4_000UL, sync.RttUs);
        Assert.AreEqual(2, sync.Samples, "a rejected-for-quality sample still counts as seen");
    }

    [TestMethod]
    public void ABetterSampleDoesReplaceTheEstimate()
    {
        var sync = new RemoteClockSync();
        RoundTrip(sync, 1_000_000, 500_000, 20_000);
        Assert.AreEqual(40_000UL, sync.RttUs);

        Assert.IsTrue(RoundTrip(sync, 3_000_000, 500_000, 1_000));
        Assert.AreEqual(2_000UL, sync.RttUs);
        Assert.AreEqual(500_000L, sync.OffsetUs);
    }

    [TestMethod]
    public void AStallIsNotAMeasurement()
    {
        // The symmetry assumption is what bounds the error, and a round trip of
        // several seconds cannot have been symmetric. Taking it would put the
        // offset out by up to a second and every later latency with it.
        var sync = new RemoteClockSync();
        Assert.IsFalse(sync.Record(0, 1_000_000, 5_000_000));
        Assert.IsFalse(sync.IsSynced);
        Assert.AreEqual(1, sync.Rejected);
        Assert.AreEqual(0, sync.Samples);
    }

    [TestMethod]
    public void APongFromBeforeItsPingIsRefused()
    {
        // Corrupt, replayed, or a mismatched id. Not a fast network.
        var sync = new RemoteClockSync();
        Assert.IsFalse(sync.Record(5_000, 1_000, 4_000));
        Assert.IsFalse(sync.IsSynced);
        Assert.AreEqual(1, sync.Rejected);
    }

    [TestMethod]
    public void AnUnsyncedEstimatorConvertsNothing()
    {
        // Falling back to the raw peer timestamp would be wrong by the entire
        // offset — which is the bug this whole type exists to end — so the
        // caller is told there is no answer instead.
        var sync = new RemoteClockSync();
        Assert.IsNull(sync.ToLocalUs(1_234_567));
        Assert.IsFalse(sync.IsSynced);
        StringAssert.Contains(sync.Summary(), "unsynced");
    }

    [TestMethod]
    public void TheMidpointSurvivesClocksDaysApart()
    {
        // Two ulong microsecond clocks days apart overflow if their sum is
        // taken. The midpoint is computed from the round trip instead.
        var sync = new RemoteClockSync();
        const ulong huge = 18_000_000_000_000;          // ~208 days of uptime
        Assert.IsTrue(sync.Record(huge, 10, huge + 10_000));
        Assert.AreEqual(10L - (long)(huge + 5_000), sync.OffsetUs);
    }
}
