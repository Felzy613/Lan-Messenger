import Foundation

// What a viewer has been allowed to do, and what the prompt that allowed it
// showed.
//
// PROTOCOL.md's consent rules make the two-stage grant a protocol requirement
// rather than an interface nicety: accepting an invite grants **viewing**, and
// control is a **separate prompt**. The reason is that the two are wildly
// different in consequence and identical in how they arrive — a single "accept"
// that quietly includes keyboard and mouse is how a screen-share feature becomes
// a remote-access one without anybody deciding to build that.
//
// So the escalation lives in a state machine rather than in a boolean, and the
// machine refuses to reach `.control` except from `.viewing`. There is no code
// path that grants control to a session nobody agreed to watch.

/// The escalation ladder. `Comparable` so a comparison reads as the ordering it
/// actually is, and so a downgrade cannot be mistaken for an upgrade by a `>`
/// that should have been `>=`.
enum RemoteGrant: String, Equatable, Comparable, CaseIterable {
    case none
    case viewing
    case control

    private var rank: Int {
        switch self {
        case .none: return 0
        case .viewing: return 1
        case .control: return 2
        }
    }

    static func < (lhs: RemoteGrant, rhs: RemoteGrant) -> Bool { lhs.rank < rhs.rank }

    /// Whether the input sub-channel may carry anything. It is inert until
    /// `control_grant` and must go inert again on revoke, end, or any error.
    var acceptsInput: Bool { self == .control }

    /// What the host indicator says. Short, because it sits in a small
    /// always-on-top strip and has to be readable at a glance.
    var indicatorLabel: String {
        switch self {
        case .none: return "Not shared"
        case .viewing: return "Screen shared"
        case .control: return "Screen and input shared"
        }
    }
}

/// One session's grant, and the rules for moving through it.
///
/// Every transition returns whether it happened. A caller that ignores the
/// result and sends `control_grant` anyway is the bug this guards against, so
/// the result is `@discardableResult`-free on purpose: the compiler warns.
struct RemoteGrantState: Equatable {

    private(set) var grant: RemoteGrant = .none
    /// A session that has ended stays ended. Reconnect means a new `session_id`,
    /// new ephemerals and new keys — so it means a new state, not this one
    /// quietly coming back to life.
    private(set) var ended = false

    init() {}

    var acceptsInput: Bool { grant.acceptsInput }
    var isLive: Bool { grant != .none && !ended }

    /// The first prompt. Grants viewing and nothing else.
    mutating func accept() -> Bool {
        guard !ended, grant == .none else { return false }
        grant = .viewing
        return true
    }

    /// The second prompt. Only reachable from `.viewing` — there is deliberately
    /// no path from `.none`, so a `control_request` that arrives before, or
    /// instead of, an accepted invite cannot be honoured.
    mutating func grantControl() -> Bool {
        guard !ended, grant == .viewing else { return false }
        grant = .control
        return true
    }

    /// Withdraws input without ending the session. The viewer keeps watching.
    mutating func revokeControl() -> Bool {
        guard !ended, grant == .control else { return false }
        grant = .viewing
        return true
    }

    /// Terminal. Capture stops, input goes inert, the indicator goes away.
    mutating func end() {
        grant = .none
        ended = true
    }
}

/// Everything the consent dialog puts on screen.
///
/// Built once, from the invite, and then only displayed — the view does no
/// policy of its own, so what a user agreed to is exactly what the gate decided
/// to ask about.
struct RemoteConsentRequest: Equatable, Identifiable {

    enum Kind: Equatable {
        /// The first prompt: someone wants to watch this screen.
        case viewing
        /// The second: they want the keyboard and mouse too.
        case control
    }

    var id: String { "\(sessionID):\(kind == .viewing ? "v" : "c")" }

    let sessionID: String
    let kind: Kind
    let peerName: String
    let peerIP: String
    let peerPublicKeyB64: String
    let trust: PeerKeyTrust
    /// When the prompt stops waiting and answers `timeout` on the user's behalf.
    let expiresAt: Date
    /// Whether this Mac can actually post the input a control grant promises.
    ///
    /// Passed in rather than read here, so the dialog stays testable without a
    /// TCC grant. Defaults to true because the viewing prompt does not depend
    /// on it at all.
    let canInject: Bool

    init(sessionID: String,
         kind: Kind,
         peerName: String,
         peerIP: String,
         peerPublicKeyB64: String,
         trust: PeerKeyTrust,
         expiresAt: Date,
         canInject: Bool = true) {
        self.sessionID = sessionID
        self.kind = kind
        self.peerName = peerName
        self.peerIP = peerIP
        self.peerPublicKeyB64 = peerPublicKeyB64
        self.trust = trust
        self.expiresAt = expiresAt
        self.canInject = canInject
    }

    /// How long a prompt waits before declining with `timeout`.
    ///
    /// A dialog that waits forever is worse than one that gives up: the screen
    /// it guards may be on a desk nobody is sitting at, behind a full-screen
    /// app, and the peer is meanwhile staring at a spinner with no way to tell a
    /// slow human from a dead one. `timeout` is already a `remote_decline`
    /// token for exactly this.
    static let defaultTimeout: TimeInterval = 45

    /// The fingerprint a user actually compares out of band. Never falls back to
    /// something reassuring: a key that cannot be parsed has no fingerprint, and
    /// the dialog must say so rather than show a plausible-looking blank.
    var fingerprint: String {
        RemoteSessionCrypto.fingerprint(publicKeyB64: peerPublicKeyB64) ?? "unreadable key"
    }

    /// The headline. Names the peer, because "someone" is not a thing a user can
    /// make a decision about.
    var title: String {
        switch kind {
        case .viewing: return "\(peerName) wants to view your screen"
        case .control: return "\(peerName) wants to control your screen"
        }
    }

    /// What is actually being agreed to, in the second person and without
    /// hedging. The control case says the quiet part out loud on purpose.
    var explanation: String {
        switch kind {
        case .viewing:
            return "They will see everything on your display, including "
                + "notifications and anything you open, until you stop sharing."
        case .control:
            return "They will be able to use your keyboard and mouse as if they "
                + "were sitting at this Mac. You can take back control at any time."
        }
    }

    /// Present only when the key is not the pinned one. Absent is meaningful —
    /// the view shows no warning chrome at all rather than a green "verified"
    /// badge, because a reassuring badge on every prompt is one nobody reads.
    var warning: String? {
        switch trust {
        case .pinned:
            return nil
        case .changedAtKnownAddress(let username, _):
            return "This is not the key you have saved for \(username). "
                + "They may have reinstalled — or this may not be them."
        case .unknown:
            return "This device is not in your contacts."
        }
    }

    /// Something about THIS Mac the user needs to know before answering, as
    /// opposed to `warning`, which is about the peer.
    ///
    /// Only one so far, and it earned its place: input injection needs the
    /// **Accessibility** grant, which is a different TCC permission from the
    /// Screen Recording one a session already has by this point. Without it
    /// `CGEvent.post` fails silently — no error, no exception, simply nothing
    /// happening — so a user grants control, watches the peer's pointer do
    /// absolutely nothing, and has no way at all to tell that from a dead
    /// network. The log says so; nobody reads the log. This is the moment the
    /// user is making the decision, so this is where it belongs.
    var systemNotice: String? {
        guard kind == .control, !canInject else { return nil }
        return "Accessibility is turned off for LAN Messenger, so their keyboard "
            + "and mouse will do nothing. Turn it on in System Settings > Privacy "
            + "& Security > Accessibility."
    }

    var acceptButtonTitle: String {
        kind == .viewing ? "Allow Viewing" : "Allow Control"
    }

    func hasExpired(at now: Date) -> Bool { now >= expiresAt }

    /// Whole seconds left, floored at zero, for the countdown.
    func secondsRemaining(at now: Date) -> Int {
        max(0, Int(expiresAt.timeIntervalSince(now).rounded(.up)))
    }
}
