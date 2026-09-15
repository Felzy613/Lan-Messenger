import Foundation

// Who may ask to see this screen, and who may be asked.
//
// This is the gate PROTOCOL.md's consent rules describe, expressed as one pure
// function so the rules are readable in one place and testable without a
// session, a socket or a window. A client that does not enforce them is not
// compatible, so they are protocol, not interface preference.
//
// Three decisions here are deliberate and easy to get wrong in the direction
// that looks friendlier:
//
//  * **A stranger gets silence, a contact gets an answer.** An invite from an
//    unpinned key is dropped without reply — not declined. A decline confirms
//    that this address runs the app and has the feature, and more importantly a
//    stranger who can provoke *any* response can use that to probe. A saved
//    contact is different: they already know all of that, and leaving them
//    hanging is the exact failure `caps` exists to prevent.
//  * **Trust is checked before the mode.** An unknown peer is ignored whether
//    the feature is on or off, so turning it on never widens who may reach you.
//  * **A changed key is ignored, not prompted.** It is not a saved contact, so
//    the consent rules say drop — but it is logged distinctly, because an
//    unfamiliar key arriving at a familiar address is the exact shape of the
//    attack the pinning defends against, and "nothing happened" is a poor
//    account of that in a bug report.

/// The user-facing switch. One setting, off by default, governing the whole
/// feature in both directions: a host that will not be viewed also does not
/// offer to view, because a single switch that only half-applies is a setting
/// users misread.
///
/// Stored as a string rather than a bool so a later mode — view-only, say — is
/// a new case rather than a config migration, and so an unrecognised value can
/// fail closed instead of being coerced to `true`.
enum RemoteDesktopMode: String, Codable, CaseIterable {
    case off
    case on

    /// Anything unrecognised is `off`. A config written by a newer build, or a
    /// corrupted one, must not leave the screen reachable — the safe direction
    /// for this particular setting is the one that does nothing.
    static func parse(_ raw: String?) -> RemoteDesktopMode {
        guard let raw, let mode = RemoteDesktopMode(rawValue: raw.lowercased()) else { return .off }
        return mode
    }

    var isEnabled: Bool { self == .on }
}

/// Why an invite was answered with `remote_decline`. The peer may show this, so
/// each case has to be true without being more specific than a contact needs.
enum RemoteDeclineReason: String, Equatable {
    /// The host has remote desktop switched off.
    case disabled
    /// A session with this peer is already in flight or live.
    case busy
}

/// Why an invite was dropped without any reply at all.
enum RemoteIgnoreReason: String, Equatable {
    case notAContact
    /// A saved contact last used this address under a different key. Ignored
    /// like any unpinned key, but worth its own name in the log.
    case keyChangedAtKnownAddress
    case selfInvite
    case malformed
}

enum RemoteInviteDecision: Equatable {
    /// Raise the consent prompt, carrying what is known about the key so the
    /// dialog can say it.
    case prompt(trust: PeerKeyTrust)
    case decline(RemoteDeclineReason)
    case ignore(RemoteIgnoreReason)

    var isPrompt: Bool {
        if case .prompt = self { return true }
        return false
    }
}

/// Everything the inbound decision depends on. Passed in rather than read from
/// singletons so the rules can be exercised exhaustively.
struct RemoteInviteContext {
    var peerPublicKeyB64: String
    var peerIP: String
    var ownPublicKeyB64: String
    var mode: RemoteDesktopMode
    var contacts: [KnownContact]
    /// Whether a session with this peer is already open, in its accept window,
    /// or live. One at a time, per peer.
    var hasSessionInFlight: Bool

    init(peerPublicKeyB64: String,
         peerIP: String,
         ownPublicKeyB64: String,
         mode: RemoteDesktopMode,
         contacts: [KnownContact],
         hasSessionInFlight: Bool = false) {
        self.peerPublicKeyB64 = peerPublicKeyB64
        self.peerIP = peerIP
        self.ownPublicKeyB64 = ownPublicKeyB64
        self.mode = mode
        self.contacts = contacts
        self.hasSessionInFlight = hasSessionInFlight
    }
}

/// Why the menu item is greyed out, in the words the interface will use.
enum RemoteUnavailableReason: String, Equatable {
    case localFeatureOff
    case peerNotAContact
    case peerOffline
    /// The peer's discovery packets carry no `remote-desktop-v1`. Sending
    /// anyway means an invite silently dropped by `PacketValidator` and an
    /// initiator waiting forever.
    case peerLacksCapability
    case sessionInFlight
}

enum RemoteInviteAvailability: Equatable {
    case available
    case unavailable(RemoteUnavailableReason)

    var isAvailable: Bool { self == .available }
}

/// What the interface knows about a peer when deciding whether to offer the
/// menu item.
struct RemoteInviteTarget {
    var isSavedContact: Bool
    var isOnline: Bool
    var advertisesRemoteDesktop: Bool
    var hasSessionInFlight: Bool

    init(isSavedContact: Bool,
         isOnline: Bool,
         advertisesRemoteDesktop: Bool,
         hasSessionInFlight: Bool = false) {
        self.isSavedContact = isSavedContact
        self.isOnline = isOnline
        self.advertisesRemoteDesktop = advertisesRemoteDesktop
        self.hasSessionInFlight = hasSessionInFlight
    }
}

enum RemoteDesktopPolicy {

    // MARK: - Inbound

    /// Decides what happens to an incoming `remote_invite`.
    ///
    /// The order of the checks is the security property, not an implementation
    /// detail: identity is settled before the local setting is consulted, so
    /// switching the feature on can never widen *who* may reach this host —
    /// only what happens for the contacts who already could.
    static func decide(_ context: RemoteInviteContext) -> RemoteInviteDecision {
        guard !context.peerPublicKeyB64.isEmpty else { return .ignore(.malformed) }
        guard context.peerPublicKeyB64 != context.ownPublicKeyB64 else {
            return .ignore(.selfInvite)
        }

        let trust = PeerKeyTrustEvaluator.evaluate(
            peerPublicKeyB64: context.peerPublicKeyB64,
            peerIP: context.peerIP,
            contacts: context.contacts)

        switch trust {
        case .unknown:
            return .ignore(.notAContact)
        case .changedAtKnownAddress:
            return .ignore(.keyChangedAtKnownAddress)
        case .pinned:
            break
        }

        // From here the peer is a saved contact, and gets a real answer.
        guard context.mode.isEnabled else { return .decline(.disabled) }
        guard !context.hasSessionInFlight else { return .decline(.busy) }
        return .prompt(trust: trust)
    }

    /// Logs the decision. Separate from `decide` so the rules stay pure and the
    /// caller can decide when a decision is worth recording — but a dropped
    /// invite must always be recorded, because the alternative account of it is
    /// nothing at all.
    static func log(_ decision: RemoteInviteDecision, peerIP: String, peerKeyB64: String) {
        let fingerprint = RemoteSessionCrypto.fingerprint(publicKeyB64: peerKeyB64) ?? "unreadable"
        switch decision {
        case .prompt:
            NetLogger.remote(event: "invite_prompt", peer: peerIP, reason: fingerprint)
        case .decline(let reason):
            NetLogger.remote(event: "invite_declined", peer: peerIP,
                             reason: "\(reason.rawValue) fp=\(fingerprint)")
        case .ignore(let reason):
            // An unfamiliar key at a familiar address is the shape of the attack
            // pinning defends against. It is dropped like any stranger, but it
            // is not a routine event and must not read as one in a bug report.
            let event = reason == .keyChangedAtKnownAddress ? "error" : "invite_ignored"
            NetLogger.remote(event: event, peer: peerIP,
                             reason: "\(reason.rawValue) fp=\(fingerprint)")
        }
    }

    // MARK: - Outbound

    /// Whether this host may offer to view `target`'s screen.
    ///
    /// Offline is reported ahead of a missing capability on purpose. Capability
    /// is learned from discovery, so a peer we have not heard from recently may
    /// have an empty record rather than a genuinely incapable one — reporting
    /// "this version does not support it" on that evidence would be a confident
    /// wrong answer where "offline" is a true and more actionable one.
    static func availability(mode: RemoteDesktopMode,
                             target: RemoteInviteTarget) -> RemoteInviteAvailability {
        guard mode.isEnabled else { return .unavailable(.localFeatureOff) }
        guard target.isSavedContact else { return .unavailable(.peerNotAContact) }
        guard target.isOnline else { return .unavailable(.peerOffline) }
        guard target.advertisesRemoteDesktop else { return .unavailable(.peerLacksCapability) }
        guard !target.hasSessionInFlight else { return .unavailable(.sessionInFlight) }
        return .available
    }
}
