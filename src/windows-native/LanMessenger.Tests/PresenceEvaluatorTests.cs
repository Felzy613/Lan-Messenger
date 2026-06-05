using LanMessenger.Core.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Guards the LAN presence state machine. See PresenceEvaluator.cs.
//
// The bug this replaces: presence was `(now - LastSeen) < 20s`, with no graceful
// "goodbye" and no active probing — peers lingered "online" for up to 20 s after
// quitting, and shortening the window reintroduced UDP-loss flicker.
[TestClass]
public class PresenceEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime SeenAgo(double seconds) => Now.AddSeconds(-seconds);

    [TestMethod]
    public void FreshHeartbeatIsOnline()
    {
        var d = PresenceEvaluator.Decide(SeenAgo(0.5), Now);
        Assert.AreEqual(PresenceEvaluator.Decision.Online, d);
        Assert.AreEqual(PeerPresence.Online, d.Presence());
        Assert.IsFalse(d.ShouldProbe());
    }

    [TestMethod]
    public void JustInsideGraceIsOnline()
    {
        // ~3 beacons of slack — a couple of dropped beacons must not flip state.
        var d = PresenceEvaluator.Decide(SeenAgo(PresenceEvaluator.OnlineGraceSeconds - 0.1), Now);
        Assert.AreEqual(PresenceEvaluator.Decision.Online, d);
    }

    [TestMethod]
    public void StalePeerProbesButStaysOnline()
    {
        // Between the grace edge and the hard cap: still shown online, but the
        // evaluator wants a liveness probe so a quiet-but-alive peer is
        // reconfirmed instead of being declared offline.
        var d = PresenceEvaluator.Decide(SeenAgo(PresenceEvaluator.OnlineGraceSeconds + 1), Now);
        Assert.AreEqual(PresenceEvaluator.Decision.Probing, d);
        Assert.AreEqual(PeerPresence.Online, d.Presence(), "probing is a grace window — must still display online");
        Assert.IsTrue(d.ShouldProbe());
    }

    [TestMethod]
    public void SilentPastHardCapIsOffline()
    {
        var d = PresenceEvaluator.Decide(SeenAgo(PresenceEvaluator.OfflineAfterSeconds + 1), Now);
        Assert.AreEqual(PresenceEvaluator.Decision.Offline, d);
        Assert.AreEqual(PeerPresence.Offline, d.Presence());
        Assert.IsFalse(d.ShouldProbe());
    }

    [TestMethod]
    public void BoundaryAtOfflineAfterIsOffline()
    {
        // Exactly at the cap is offline (the probing window is half-open).
        var d = PresenceEvaluator.Decide(SeenAgo(PresenceEvaluator.OfflineAfterSeconds), Now);
        Assert.AreEqual(PresenceEvaluator.Decision.Offline, d);
    }

    [TestMethod]
    public void WindowsAreOrderedAndShorterThanLegacyTimeout()
    {
        // Sanity on the tunables: grace < cap, and the whole machine resolves
        // well before the old 20 s timeout that felt broken.
        Assert.IsTrue(PresenceEvaluator.OnlineGraceSeconds < PresenceEvaluator.OfflineAfterSeconds);
        Assert.IsTrue(PresenceEvaluator.OfflineAfterSeconds < 20);
    }
}
