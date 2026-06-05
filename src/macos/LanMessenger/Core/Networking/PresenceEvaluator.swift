import Foundation

// Displayed presence for a peer. Binary by design — the UI shows a green or a
// gray dot. The "probing" middle state of the evaluator still displays as
// `.online` (it is a grace window, not a separate user-visible status).
enum PeerPresence: Equatable {
    case online
    case offline
}

// Pure decision core of the LAN presence state machine. No sockets, no clocks
// captured internally — `now` is always passed in so the whole thing is
// deterministic and unit-testable.
//
// Presence is driven by `lastSeen`, refreshed by any heartbeat (discovery
// beacon, discovery_reply, or any inbound TCP packet). An explicit `goodbye`
// datagram bypasses this evaluator and forces `.offline` immediately; this
// evaluator only governs the silent-peer case (crash, sleep without goodbye,
// cable pull).
//
// The middle `.probing` window is what lets the offline timeout be short
// without flicker: a peer that has gone quiet is *actively* unicast-pinged
// before we declare it offline, rather than passively assumed dead the instant
// a few beacons are lost.
enum PresenceEvaluator {

    // Tunable timings. Beacon interval is 1.5 s (PROTOCOL.md), so each window
    // below is expressed in whole beacons of slack.
    //
    // [0, onlineGrace)        → online, no action
    // [onlineGrace, offlineAfter) → online, actively probe each tick
    // [offlineAfter, ∞)       → offline
    static let onlineGrace:  TimeInterval = 5    // ~3 missed beacons of slack
    static let offlineAfter: TimeInterval = 12   // hard cap for silent peers

    enum Decision: Equatable {
        case online    // heard recently — healthy
        case probing   // gone quiet — still shown online, but ping to reconfirm
        case offline   // silent past the hard cap — gone

        // Display state. `.probing` is a grace window and still reads online.
        var presence: PeerPresence { self == .offline ? .offline : .online }

        // Whether the evaluator wants an active liveness probe sent this tick.
        var shouldProbe: Bool { self == .probing }
    }

    static func decide(lastSeen: Date, now: Date = Date()) -> Decision {
        let age = now.timeIntervalSince(lastSeen)
        if age < onlineGrace  { return .online }
        if age < offlineAfter { return .probing }
        return .offline
    }
}
