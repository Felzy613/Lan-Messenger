namespace LanMessenger.Core.Persistence;

// Centralised lifecycle ranking for a message's status field.
//
// The pipeline can race on cross-platform LANs: the receiver's "sent_receipt"
// (which maps to "Delivered") frequently arrives on the UI thread BEFORE the
// sender's own "Sent" dispatch (the TCP write completion). Without ranking,
// the late "Sent" overwrites "Delivered" — the user is left with one tick
// forever even though the peer actually delivered the message. This was the
// root cause of intermittent Mac↔Windows interop failures: machines with
// higher thread-pool contention reliably lost the race.
//
// Rule: monotonic upgrade only. A lower-ranked status never overwrites a
// higher-ranked one. "Failed" is terminal and only fires before the wire
// send begins, so it sits below the in-flight states.
public static class MessageStatus
{
    public const string Failed    = "Failed";
    public const string Sending   = "Sending";
    public const string Queued    = "Queued";
    public const string Sent      = "Sent";
    public const string Delivered = "Delivered";
    public const string Read      = "Read";

    public static int Rank(string? status) => status switch
    {
        Failed    => -1,
        Sent      => 2,
        Delivered => 3,
        Read      => 4,
        // "", "Sending", "Queued" and unknown values all sit at 1 — they
        // represent in-flight / no-progress states and may be upgraded by
        // anything higher.
        _ => 1,
    };

    // Returns true iff `next` should replace `current`. Monotonic upgrade
    // policy with one exception: same-rank transitions are allowed so that
    // "Queued" → "Sending" → re-send flows still update the UI.
    public static bool ShouldApply(string? next, string? current)
        => Rank(next) >= Rank(current);
}
