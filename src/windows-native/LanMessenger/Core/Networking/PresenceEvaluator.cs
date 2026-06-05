namespace LanMessenger.Core.Networking;

// Displayed presence for a peer. Binary by design — the UI shows a green or a
// gray dot. The "Probing" middle state of the evaluator still displays as
// Online (it is a grace window, not a separate user-visible status).
public enum PeerPresence
{
    Online,
    Offline,
}

// Pure decision core of the LAN presence state machine. No sockets, no clocks
// captured internally — `now` is always passed in so the whole thing is
// deterministic and unit-testable. Mirror of the Swift PresenceEvaluator.
//
// Presence is driven by LastSeen, refreshed by any heartbeat (discovery beacon,
// discovery_reply, or any inbound TCP packet). An explicit "goodbye" datagram
// bypasses this evaluator and forces Offline immediately; this evaluator only
// governs the silent-peer case (crash, sleep without goodbye, cable pull).
//
// The middle Probing window is what lets the offline timeout be short without
// flicker: a peer that has gone quiet is actively unicast-pinged before we
// declare it offline, rather than passively assumed dead the instant a few
// beacons are lost.
public static class PresenceEvaluator
{
    public enum Decision
    {
        Online,    // heard recently — healthy
        Probing,   // gone quiet — still shown online, but ping to reconfirm
        Offline,   // silent past the hard cap — gone
    }

    // Tunable timings (seconds). Beacon interval is 1.5 s (PROTOCOL.md).
    //
    // [0, OnlineGrace)            → Online, no action
    // [OnlineGrace, OfflineAfter) → Online, actively probe each tick
    // [OfflineAfter, ∞)          → Offline
    public const double OnlineGraceSeconds  = 5;    // ~3 missed beacons of slack
    public const double OfflineAfterSeconds = 12;   // hard cap for silent peers

    public static Decision Decide(DateTime lastSeen, DateTime now)
    {
        var age = (now - lastSeen).TotalSeconds;
        if (age < OnlineGraceSeconds)  return Decision.Online;
        if (age < OfflineAfterSeconds) return Decision.Probing;
        return Decision.Offline;
    }

    // Display state. Probing is a grace window and still reads online.
    public static PeerPresence Presence(this Decision d) =>
        d == Decision.Offline ? PeerPresence.Offline : PeerPresence.Online;

    // Whether the evaluator wants an active liveness probe sent this tick.
    public static bool ShouldProbe(this Decision d) => d == Decision.Probing;
}
