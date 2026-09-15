using LanMessenger.Core.Crypto;
using LanMessenger.Core.Services;

namespace LanMessenger.Core.Networking.Media;

// Who may ask to see this screen, and who may be asked. Mirror of the macOS
// RemoteDesktopPolicy.swift — the two must agree, because these are protocol
// requirements rather than interface preferences: "a client that does not
// enforce them is not compatible".
//
// Three decisions here are deliberate and easy to get wrong in the direction
// that looks friendlier:
//
//  * A stranger gets silence, a contact gets an answer. An invite from an
//    unpinned key is dropped without reply — not declined. A decline confirms
//    that this address runs the app and has the feature, and a stranger who can
//    provoke *any* response can use that to probe. A saved contact is different:
//    they already know all of that, and leaving them hanging is the exact
//    failure `caps` exists to prevent.
//  * Trust is checked before the mode. An unknown peer is ignored whether the
//    feature is on or off, so turning it on never widens who may reach you.
//  * A changed key is ignored, not prompted. It is not a saved contact, so the
//    consent rules say drop — but it is logged distinctly, because an
//    unfamiliar key arriving at a familiar address is the exact shape of the
//    attack pinning defends against, and "nothing happened" is a poor account of
//    that in a bug report.

/// The user-facing switch. One setting, off by default, governing the whole
/// feature in both directions: a host that will not be viewed also does not
/// offer to view, because a single switch that only half-applies is a setting
/// users misread.
///
/// Stored as a string rather than a bool so a later mode is a new case rather
/// than a config migration, and so an unrecognised value can fail closed instead
/// of being coerced to true.
public enum RemoteDesktopMode
{
    Off,
    On,
}

public static class RemoteDesktopModeParser
{
    public const string OffToken = "off";
    public const string OnToken = "on";

    /// Anything unrecognised is Off. A config written by a newer build, or a
    /// corrupted one, must not leave the screen reachable — the safe direction
    /// for this particular setting is the one that does nothing.
    public static RemoteDesktopMode Parse(string? raw) =>
        raw?.ToLowerInvariant() == OnToken ? RemoteDesktopMode.On : RemoteDesktopMode.Off;

    public static string ToToken(this RemoteDesktopMode mode) =>
        mode == RemoteDesktopMode.On ? OnToken : OffToken;

    public static bool IsEnabled(this RemoteDesktopMode mode) => mode == RemoteDesktopMode.On;
}

/// Why an invite was answered with remote_decline. The peer may show this, so
/// each case has to be true without being more specific than a contact needs.
public enum RemoteDeclineReason
{
    /// The host has remote desktop switched off.
    Disabled,
    /// A session with this peer is already in flight or live.
    Busy,
}

/// Why an invite was dropped without any reply at all.
public enum RemoteIgnoreReason
{
    NotAContact,
    /// A saved contact last used this address under a different key. Ignored
    /// like any unpinned key, but worth its own name in the log.
    KeyChangedAtKnownAddress,
    SelfInvite,
    Malformed,
}

public enum RemoteInviteAction { Prompt, Decline, Ignore }

public readonly record struct RemoteInviteDecision(
    RemoteInviteAction Action,
    PeerKeyTrust Trust,
    RemoteDeclineReason DeclineReason,
    RemoteIgnoreReason IgnoreReason)
{
    public static RemoteInviteDecision Prompt(PeerKeyTrust trust) =>
        new(RemoteInviteAction.Prompt, trust, default, default);

    public static RemoteInviteDecision Decline(RemoteDeclineReason reason) =>
        new(RemoteInviteAction.Decline, PeerKeyTrust.Unknown, reason, default);

    public static RemoteInviteDecision Ignore(RemoteIgnoreReason reason) =>
        new(RemoteInviteAction.Ignore, PeerKeyTrust.Unknown, default, reason);

    public bool IsPrompt => Action == RemoteInviteAction.Prompt;
}

/// Everything the inbound decision depends on. Passed in rather than read from
/// singletons so the rules can be exercised exhaustively.
public sealed class RemoteInviteContext
{
    public string PeerPublicKeyB64 { get; init; } = "";
    public string PeerIP { get; init; } = "";
    public string OwnPublicKeyB64 { get; init; } = "";
    public RemoteDesktopMode Mode { get; init; } = RemoteDesktopMode.Off;
    public IReadOnlyList<KnownContact> Contacts { get; init; } = [];
    /// Whether a session with this peer is already open, in its accept window, or
    /// live. One at a time, per peer.
    public bool HasSessionInFlight { get; init; }
}

/// Why the menu item is greyed out, in the words the interface will use.
public enum RemoteUnavailableReason
{
    None,
    LocalFeatureOff,
    PeerNotAContact,
    PeerOffline,
    /// The peer's discovery packets carry no remote-desktop-v1. Sending anyway
    /// means an invite silently dropped by PacketValidator and an initiator
    /// waiting forever.
    PeerLacksCapability,
    SessionInFlight,
}

public readonly record struct RemoteInviteAvailability(bool IsAvailable, RemoteUnavailableReason Reason)
{
    public static RemoteInviteAvailability Available => new(true, RemoteUnavailableReason.None);
    public static RemoteInviteAvailability Unavailable(RemoteUnavailableReason reason) =>
        new(false, reason);
}

/// What the interface knows about a peer when deciding whether to offer the menu
/// item.
public sealed class RemoteInviteTarget
{
    public bool IsSavedContact { get; init; }
    public bool IsOnline { get; init; }
    public bool AdvertisesRemoteDesktop { get; init; }
    public bool HasSessionInFlight { get; init; }
}

public static class RemoteDesktopPolicy
{
    // ---- Inbound -----------------------------------------------------------

    /// Decides what happens to an incoming remote_invite.
    ///
    /// The order of the checks is the security property, not an implementation
    /// detail: identity is settled before the local setting is consulted, so
    /// switching the feature on can never widen *who* may reach this host — only
    /// what happens for the contacts who already could.
    public static RemoteInviteDecision Decide(RemoteInviteContext context)
    {
        if (string.IsNullOrEmpty(context.PeerPublicKeyB64))
        {
            return RemoteInviteDecision.Ignore(RemoteIgnoreReason.Malformed);
        }
        if (context.PeerPublicKeyB64 == context.OwnPublicKeyB64)
        {
            return RemoteInviteDecision.Ignore(RemoteIgnoreReason.SelfInvite);
        }

        var trust = PeerKeyTrustEvaluator.Evaluate(
            context.PeerPublicKeyB64, context.PeerIP, context.Contacts);

        switch (trust.Kind)
        {
            case PeerKeyTrustKind.Unknown:
                return RemoteInviteDecision.Ignore(RemoteIgnoreReason.NotAContact);
            case PeerKeyTrustKind.ChangedAtKnownAddress:
                return RemoteInviteDecision.Ignore(RemoteIgnoreReason.KeyChangedAtKnownAddress);
        }

        // From here the peer is a saved contact, and gets a real answer.
        if (!context.Mode.IsEnabled())
        {
            return RemoteInviteDecision.Decline(RemoteDeclineReason.Disabled);
        }
        if (context.HasSessionInFlight)
        {
            return RemoteInviteDecision.Decline(RemoteDeclineReason.Busy);
        }
        return RemoteInviteDecision.Prompt(trust);
    }

    /// Logs the decision. Separate from Decide so the rules stay pure and the
    /// caller can decide when a decision is worth recording — but a dropped
    /// invite must always be recorded, because the alternative account of it is
    /// nothing at all.
    public static void Log(RemoteInviteDecision decision, string peerIP, string peerKeyB64)
    {
        var fingerprint = RemoteSessionCrypto.Fingerprint(peerKeyB64) ?? "unreadable";
        switch (decision.Action)
        {
            case RemoteInviteAction.Prompt:
                LanLogger.Remote("invite_prompt", peer: peerIP, reason: fingerprint);
                break;
            case RemoteInviteAction.Decline:
                LanLogger.Remote("invite_declined", peer: peerIP,
                    reason: $"{decision.DeclineReason.ToString().ToLowerInvariant()} fp={fingerprint}");
                break;
            default:
                // An unfamiliar key at a familiar address is the shape of the
                // attack pinning defends against. It is dropped like any
                // stranger, but it is not a routine event and must not read as
                // one in a bug report.
                var evt = decision.IgnoreReason == RemoteIgnoreReason.KeyChangedAtKnownAddress
                    ? "error" : "invite_ignored";
                LanLogger.Remote(evt, peer: peerIP,
                    reason: $"{decision.IgnoreReason.ToString().ToLowerInvariant()} fp={fingerprint}");
                break;
        }
    }

    // ---- Outbound ----------------------------------------------------------

    /// Whether this host may offer to view the target's screen.
    ///
    /// Offline is reported ahead of a missing capability on purpose. Capability
    /// is learned from discovery, so a peer we have not heard from recently may
    /// have an empty record rather than a genuinely incapable one — reporting
    /// "this version does not support it" on that evidence would be a confident
    /// wrong answer where "offline" is a true and more actionable one.
    public static RemoteInviteAvailability Availability(
        RemoteDesktopMode mode, RemoteInviteTarget target)
    {
        if (!mode.IsEnabled())
        {
            return RemoteInviteAvailability.Unavailable(RemoteUnavailableReason.LocalFeatureOff);
        }
        if (!target.IsSavedContact)
        {
            return RemoteInviteAvailability.Unavailable(RemoteUnavailableReason.PeerNotAContact);
        }
        if (!target.IsOnline)
        {
            return RemoteInviteAvailability.Unavailable(RemoteUnavailableReason.PeerOffline);
        }
        if (!target.AdvertisesRemoteDesktop)
        {
            return RemoteInviteAvailability.Unavailable(RemoteUnavailableReason.PeerLacksCapability);
        }
        if (target.HasSessionInFlight)
        {
            return RemoteInviteAvailability.Unavailable(RemoteUnavailableReason.SessionInFlight);
        }
        return RemoteInviteAvailability.Available;
    }
}
