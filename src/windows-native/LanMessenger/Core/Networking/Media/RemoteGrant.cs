using LanMessenger.Core.Crypto;

namespace LanMessenger.Core.Networking.Media;

// What a viewer has been allowed to do. Mirror of RemoteGrant.swift.
//
// PROTOCOL.md makes the two-stage grant a requirement rather than an interface
// nicety: accepting an invite grants viewing, and control is a separate prompt.
// The two are wildly different in consequence and identical in how they arrive,
// so a single "accept" that quietly includes keyboard and mouse is how a
// screen-share feature becomes a remote-access one without anybody deciding to
// build that.

/// The escalation ladder. Ordered so a comparison reads as the ordering it is,
/// and a downgrade cannot be mistaken for an upgrade.
public enum RemoteGrant { None = 0, Viewing = 1, Control = 2 }

public static class RemoteGrantExtensions
{
    /// Whether the input sub-channel may carry anything. It is inert until
    /// control_grant and must go inert again on revoke, end, or any error.
    public static bool AcceptsInput(this RemoteGrant grant) => grant == RemoteGrant.Control;

    /// What the host indicator says. Short, because it sits in a small
    /// always-on-top strip and has to be readable at a glance.
    public static string IndicatorLabel(this RemoteGrant grant) => grant switch
    {
        RemoteGrant.Viewing => "Screen shared",
        RemoteGrant.Control => "Screen and input shared",
        _ => "Not shared",
    };
}

/// One session's grant, and the rules for moving through it. Every transition
/// reports whether it happened; a caller that ignores the result and sends
/// control_grant anyway is the bug this guards against.
public sealed class RemoteGrantState
{
    public RemoteGrant Grant { get; private set; } = RemoteGrant.None;

    /// A session that has ended stays ended. Reconnect means a new session_id,
    /// new ephemerals and new keys — so it means a new state, not this one
    /// quietly coming back to life.
    public bool Ended { get; private set; }

    public bool AcceptsInput => Grant.AcceptsInput();
    public bool IsLive => Grant != RemoteGrant.None && !Ended;

    /// The first prompt. Grants viewing and nothing else.
    public bool Accept()
    {
        if (Ended || Grant != RemoteGrant.None) return false;
        Grant = RemoteGrant.Viewing;
        return true;
    }

    /// The second prompt. Only reachable from Viewing — there is deliberately no
    /// path from None, so a control_request that arrives before, or instead of,
    /// an accepted invite cannot be honoured.
    public bool GrantControl()
    {
        if (Ended || Grant != RemoteGrant.Viewing) return false;
        Grant = RemoteGrant.Control;
        return true;
    }

    /// Withdraws input without ending the session. The viewer keeps watching.
    public bool RevokeControl()
    {
        if (Ended || Grant != RemoteGrant.Control) return false;
        Grant = RemoteGrant.Viewing;
        return true;
    }

    /// Terminal. Capture stops, input goes inert, the indicator goes away.
    public void End()
    {
        Grant = RemoteGrant.None;
        Ended = true;
    }
}

/// <summary>Which of the two consents is being asked for.</summary>
public enum RemoteConsentKind { Viewing, Control }

public enum RemoteConsentOutcomeKind { Accepted, Declined, TimedOut }

/// <summary>
/// How a consent prompt ended, and why.
///
/// The reason travels with the outcome rather than being inferred from it,
/// because PROTOCOL.md distinguishes a person saying no from a prompt nobody
/// answered — `declined` and `timeout` are different tokens on the wire, and an
/// initiator is entitled to know which it got.
/// </summary>
public readonly record struct RemoteConsentOutcome(
    RemoteConsentOutcomeKind Kind, RemoteDeclineReason Reason)
{
    public static RemoteConsentOutcome Accepted =>
        new(RemoteConsentOutcomeKind.Accepted, RemoteDeclineReason.Declined);

    public static RemoteConsentOutcome Declined(
        RemoteDeclineReason reason = RemoteDeclineReason.Declined) =>
        new(RemoteConsentOutcomeKind.Declined, reason);

    public static RemoteConsentOutcome TimedOut =>
        new(RemoteConsentOutcomeKind.TimedOut, RemoteDeclineReason.Timeout);
}

/// <summary>
/// Everything a consent prompt needs to say. Mirror of the Swift type.
///
/// A value rather than a pile of constructor arguments so that the decision to
/// prompt, the prompt itself, and the test that exercises the decision all agree
/// on what a request is. The fingerprint in particular has to be derived the
/// same way everywhere: a display name is trivially spoofable by anyone on the
/// LAN, and the pinned key is the only thing in here that is not.
/// </summary>
public sealed record RemoteConsentRequest(
    string SessionId,
    RemoteConsentKind Kind,
    string PeerName,
    string PeerIP,
    string PeerPublicKeyB64,
    PeerKeyTrust Trust,
    DateTime ExpiresAt)
{
    /// <summary>How long a prompt waits before declining on the user's behalf.</summary>
    /// <remarks>
    /// A dialog that waits forever is worse than one that gives up: the screen
    /// it guards may be on a desk nobody is sitting at, and the peer is
    /// meanwhile staring at a spinner with no way to tell a slow human from a
    /// dead one.
    /// </remarks>
    public const int DefaultTimeoutSeconds = 45;

    /// <summary>Never falls back to something reassuring.</summary>
    public string Fingerprint =>
        RemoteSessionCrypto.Fingerprint(PeerPublicKeyB64) ?? "unreadable key";

    public int SecondsRemaining(DateTime now) =>
        Math.Max(0, (int)Math.Ceiling((ExpiresAt - now).TotalSeconds));
}
