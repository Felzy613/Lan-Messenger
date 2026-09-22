import Foundation

// Every way a session ends, and the shortcut that must always be able to end it.
//
// PROTOCOL.md requires two things that look small and are not: the host reserves
// "a kill shortcut that is never forwarded to the peer, so a host being actively
// controlled can always stop the session", and the session auto-stops on screen
// lock, user switch, sleep, network loss and app quit.
//
// Both exist for the same reason. Once control has been granted, the host's own
// mouse is contested — the viewer is moving it too — so a Stop button is not a
// guarantee, it is a race. The kill shortcut is the guarantee, and the auto-stop
// triggers are the cases where nobody is present to press anything.

/// Why a session stopped. Persisted into the audit trail and, where the link is
/// still alive, sent to the peer as a `remote_end`.
enum RemoteStopReason: String, Equatable, CaseIterable {
    /// The host pressed Stop on the indicator.
    case userStopped = "user_stopped"
    /// The host used the reserved kill shortcut.
    case killSwitch = "kill_switch"
    /// The screen locked. Continuing would share a lock screen the host cannot
    /// see themselves, and whatever is behind it when they come back.
    case screenLocked = "screen_locked"
    /// Fast user switching. The session belongs to the account that agreed to
    /// it, not to whoever sat down next.
    case userSwitched = "user_switched"
    case systemSleep = "system_sleep"
    /// No usable interface. The link is already gone; this is bookkeeping.
    case networkLost = "network_lost"
    case appQuit = "app_quit"
    /// No inbound traffic for the watchdog timeout — a crashed or wedged viewer.
    case watchdog = "watchdog"
    /// The peer sent `remote_end`.
    case peerEnded = "peer_ended"
    case error

    /// Whether a `remote_end` can still be put on the wire. The distinction is
    /// not politeness: a peer that gets no `remote_end` waits out its own
    /// watchdog before it believes the session is over, and shows a frozen last
    /// frame the whole time.
    var canNotifyPeer: Bool {
        switch self {
        case .networkLost, .peerEnded, .error: return false
        default: return true
        }
    }

    /// Whether the host chose this. Shapes the audit line, and whether anything
    /// is worth surfacing to the user afterwards — a session that ended because
    /// somebody pressed Stop needs no explanation; one that ended because a
    /// watchdog fired does.
    var isDeliberate: Bool {
        switch self {
        case .userStopped, .killSwitch, .appQuit: return true
        default: return false
        }
    }

    /// Why a media channel closing ended the session.
    ///
    /// A channel closes two ways and they are not the same event. The peer
    /// pressing Stop closes the socket cleanly, with no error; a network that
    /// went away closes it with one. Recording both as `networkLost` put "the
    /// session ended because the network connection was lost" in the history of
    /// every session the *other side* ended deliberately — which is a false
    /// entry in the one record a user consults to find out what happened, and
    /// it was there while the log one line above said `peer ended`.
    ///
    /// `fallback` is what an errored close means, which differs by role and by
    /// platform, so the caller keeps saying it.
    static func forChannelClose(error: Error?, fallback: RemoteStopReason) -> RemoteStopReason {
        error == nil ? .peerEnded : fallback
    }

    /// One line for the conversation's audit trail, in the past tense, naming
    /// the cause rather than the mechanism.
    ///
    /// **Takes the role**, because every one of these sentences used to be
    /// written from the host's chair. A Mac that had spent ten minutes watching
    /// somebody else's screen ended the session and wrote "You stopped sharing
    /// your screen." into the thread — which is not a wording slip but a false
    /// record, in the one place a user goes to find out whether their screen
    /// was ever shared.
    func auditDescription(viewing: Bool) -> String {
        if viewing { return viewerDescription }
        return hostDescription
    }

    private var hostDescription: String {
        switch self {
        case .userStopped:  return "You stopped sharing your screen."
        case .killSwitch:   return "You stopped sharing your screen with the emergency shortcut."
        case .screenLocked: return "Screen sharing stopped because this Mac was locked."
        case .userSwitched: return "Screen sharing stopped because the user account was switched."
        case .systemSleep:  return "Screen sharing stopped because this Mac went to sleep."
        case .networkLost:  return "Screen sharing stopped because the network connection was lost."
        case .appQuit:      return "Screen sharing stopped because LAN Messenger quit."
        case .watchdog:     return "Screen sharing stopped because the other side stopped responding."
        case .peerEnded:    return "The other side ended the session."
        case .error:        return "Screen sharing stopped because of an error."
        }
    }

    /// The same causes, said by the machine that was doing the watching. No
    /// sentence here claims anything about our own screen, because nothing was
    /// captured on this side at all.
    private var viewerDescription: String {
        switch self {
        case .userStopped:  return "You stopped viewing their screen."
        case .killSwitch:   return "You stopped viewing their screen with the emergency shortcut."
        case .screenLocked: return "The session ended because this Mac was locked."
        case .userSwitched: return "The session ended because the user account was switched."
        case .systemSleep:  return "The session ended because this Mac went to sleep."
        case .networkLost:  return "The session ended because the network connection was lost."
        case .appQuit:      return "The session ended because LAN Messenger quit."
        case .watchdog:     return "The session ended because the other side stopped responding."
        case .peerEnded:    return "The other side ended the session."
        case .error:        return "The session ended because of an error."
        }
    }
}

/// A key combination, in the Carbon terms `RegisterEventHotKey` speaks.
struct RemoteShortcut: Equatable {
    /// A `kVK_*` virtual key code.
    let keyCode: UInt32
    /// A Carbon modifier mask (`controlKey | optionKey | cmdKey | shiftKey`).
    let modifiers: UInt32
    /// What the interface calls it.
    let displayName: String
}

/// The shortcut that always stops a session.
enum RemoteKillSwitch {

    /// `⌃⌥⌘⎋`.
    ///
    /// Escape is never a text character, and three modifiers put it well out of
    /// reach of an accidental press. Its neighbour is deliberate: `⌘⌥⎋` is Force
    /// Quit, so a host groping for the escape hatch under stress lands on either
    /// this — a clean stop, with a `remote_end` and an audit line — or on Force
    /// Quit, which kills the app and therefore the session too. Both exits work.
    /// A shortcut nobody can remember in a panic is not a safety feature.
    ///
    /// Registered through Carbon's `RegisterEventHotKey`, which needs **no TCC
    /// grant at all**. That matters more than it looks: the Accessibility grant
    /// that input injection depends on can be absent or revoked, and the kill
    /// switch has to work in exactly the situations where other things have
    /// gone wrong.
    static let shortcut = RemoteShortcut(
        keyCode: 0x35,                       // kVK_Escape
        modifiers: 0x1000 | 0x0800 | 0x0100, // controlKey | optionKey | cmdKey
        displayName: "⌃⌥⌘⎋")

    /// Combinations a viewer's input capture must never put on the wire.
    ///
    /// The protocol requirement is that the kill shortcut is *never forwarded*.
    /// On the viewer side that means the capture layer swallows it locally
    /// rather than sending it, so a viewer can always escape their own session
    /// too — and so a host's kill shortcut can never be triggered remotely by
    /// the very peer it exists to stop.
    ///
    /// WS7 has not been built yet. This is here now, with a test, so the rule
    /// exists before the code that has to obey it.
    static let reserved: [RemoteShortcut] = [shortcut]

    static func isReserved(keyCode: UInt32, modifiers: UInt32) -> Bool {
        reserved.contains { $0.keyCode == keyCode && $0.modifiers == modifiers }
    }
}
