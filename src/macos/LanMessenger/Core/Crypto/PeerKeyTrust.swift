import Foundation

// What we know about the identity key behind an incoming remote-desktop invite.
//
// PROTOCOL.md's consent rules require the prompt to show the peer's name *and*
// identity key fingerprint, and to **distinguish a key matching the saved
// contact from a new or changed one**. That distinction is the whole security
// value of the prompt: a display name is trivially spoofable by anyone on the
// LAN, and so is an IP address. The pinned key is not.
//
// The awkward part is that contacts are keyed *by* public key, so a peer whose
// key changes does not look like a changed contact — it looks like a brand new
// one, indistinguishable from a stranger. The signal that something changed is
// therefore positional: a saved contact used to live at this address under a
// different key. That is exactly the shape of both the innocent case (they
// reinstalled, or wiped the app) and the alarming one (somebody is standing
// where they were), and the prompt cannot tell them apart — so it says so and
// lets the human decide, which is the only correct behaviour available.

/// A contact reduced to the three fields trust evaluation needs. Deliberately
/// not `ContactConfig`: this stays free of persistence types so it can be
/// evaluated against any source, including a test's literal list.
struct KnownContact: Equatable {
    let publicKeyB64: String
    let username: String
    let lastIP: String

    init(publicKeyB64: String, username: String, lastIP: String) {
        self.publicKeyB64 = publicKeyB64
        self.username = username
        self.lastIP = lastIP
    }
}

enum PeerKeyTrust: Equatable {
    /// The key matches a saved contact. The only case that may be presented
    /// without a warning.
    case pinned(username: String)

    /// A saved contact last lived at this address under a *different* key.
    /// Innocent or not, the user is the only one who can tell.
    case changedAtKnownAddress(username: String, previousPublicKeyB64: String)

    /// No saved contact holds this key. Per the consent rules an invite from
    /// one of these is dropped without prompting — a stranger must not be able
    /// to raise a dialog on someone's screen, which is itself an attack.
    case unknown

    var isPinned: Bool {
        if case .pinned = self { return true }
        return false
    }

    /// Whether the prompt must carry a warning. `unknown` counts: an invite from
    /// an unknown key should never reach a prompt at all, so if one does, the
    /// warning is the last line of defence rather than the first.
    var requiresWarning: Bool { !isPinned }
}

enum PeerKeyTrustEvaluator {

    /// Classifies the key behind an invite.
    ///
    /// Key match wins over address match, and deliberately so: peers roam, and
    /// the app already migrates conversation history when a saved contact turns
    /// up on a new IP. A pinned key arriving from an unfamiliar address is the
    /// normal case of somebody changing network, not a warning.
    static func evaluate(
        peerPublicKeyB64: String,
        peerIP: String,
        contacts: [KnownContact]
    ) -> PeerKeyTrust {
        guard !peerPublicKeyB64.isEmpty else { return .unknown }

        if let pinned = contacts.first(where: { $0.publicKeyB64 == peerPublicKeyB64 }) {
            return .pinned(username: pinned.username)
        }

        // An unfamiliar key at a familiar address. If several contacts have
        // lived here, the first is reported — the point is to raise the
        // question, and naming one previous occupant does that as well as
        // naming three.
        if !peerIP.isEmpty,
           let displaced = contacts.first(where: { $0.lastIP == peerIP && !$0.lastIP.isEmpty }) {
            return .changedAtKnownAddress(username: displaced.username,
                                          previousPublicKeyB64: displaced.publicKeyB64)
        }

        return .unknown
    }
}
