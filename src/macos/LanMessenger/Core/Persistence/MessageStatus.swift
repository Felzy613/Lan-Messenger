import Foundation

// Centralised lifecycle ranking for a message's status field.
//
// The pipeline can race on cross-platform LANs: the receiver's "sent_receipt"
// (which maps to "Delivered") frequently arrives on the main queue BEFORE the
// sender's own "Sent" dispatch (the TCP-write completion). Without ranking,
// the late "Sent" overwrites "Delivered" — the user is left with one tick
// forever even though the peer actually delivered the message. This was the
// root cause of intermittent Mac↔Windows interop failures: machines with
// higher CPU/network load reliably lost the race.
//
// Rule: monotonic upgrade only. A lower-ranked status never overwrites a
// higher-ranked one. "Failed" is terminal and only fires before the wire
// send begins, so it sits below the in-flight states.
enum MessageStatus {
    static let failed    = "Failed"
    static let sending   = "Sending"
    static let queued    = "Queued"
    static let sent      = "Sent"
    static let delivered = "Delivered"
    static let read      = "Read"

    static func rank(_ status: String?) -> Int {
        switch status {
        case Self.failed:    return -1
        case Self.sent:      return 2
        case Self.delivered: return 3
        case Self.read:      return 4
        default:             return 1   // "", "Sending", "Queued", unknown
        }
    }

    // Returns true iff `next` should replace `current`. Monotonic upgrade
    // policy; same-rank transitions are allowed so that Queued → Sending
    // and re-send flows still update the UI.
    static func shouldApply(_ next: String?, over current: String?) -> Bool {
        rank(next) >= rank(current)
    }
}
