namespace LanMessenger.Core.Crypto;

// What we know about the identity key behind an incoming remote-desktop invite.
// Mirror of the macOS PeerKeyTrust.swift.
//
// PROTOCOL.md's consent rules require the prompt to show the peer's name *and*
// identity key fingerprint, and to distinguish a key matching the saved contact
// from a new or changed one. That distinction is the whole security value of the
// prompt: a display name is trivially spoofable by anyone on the LAN, and so is
// an IP address. The pinned key is not.
//
// The awkward part is that contacts are keyed *by* public key, so a peer whose
// key changes does not look like a changed contact — it looks like a brand new
// one, indistinguishable from a stranger. The signal that something changed is
// therefore positional: a saved contact used to live at this address under a
// different key. That is the shape of both the innocent case (they reinstalled)
// and the alarming one (somebody is standing where they were), and nothing can
// tell them apart — so it says so and lets the human decide.

/// A contact reduced to the three fields trust evaluation needs. Deliberately
/// not ContactConfig: this stays free of persistence types so it can be
/// evaluated against any source, including a test's literal list.
public readonly record struct KnownContact(string PublicKeyB64, string Username, string LastIP);

public enum PeerKeyTrustKind
{
    /// The key matches a saved contact. The only case that may be presented
    /// without a warning.
    Pinned,

    /// A saved contact last lived at this address under a *different* key.
    /// Innocent or not, the user is the only one who can tell.
    ChangedAtKnownAddress,

    /// No saved contact holds this key. Per the consent rules an invite from one
    /// of these is dropped without prompting — a stranger must not be able to
    /// raise a dialog on someone's screen, which is itself an attack.
    Unknown,
}

public readonly record struct PeerKeyTrust(
    PeerKeyTrustKind Kind,
    string Username,
    string PreviousPublicKeyB64)
{
    public static PeerKeyTrust Pinned(string username) =>
        new(PeerKeyTrustKind.Pinned, username, "");

    public static PeerKeyTrust ChangedAtKnownAddress(string username, string previousKeyB64) =>
        new(PeerKeyTrustKind.ChangedAtKnownAddress, username, previousKeyB64);

    public static PeerKeyTrust Unknown => new(PeerKeyTrustKind.Unknown, "", "");

    public bool IsPinned => Kind == PeerKeyTrustKind.Pinned;

    /// Whether the prompt must carry a warning. Unknown counts: an invite from an
    /// unknown key should never reach a prompt at all, so if one does, the
    /// warning is the last line of defence rather than the first.
    public bool RequiresWarning => !IsPinned;
}

public static class PeerKeyTrustEvaluator
{
    /// Classifies the key behind an invite.
    ///
    /// Key match wins over address match, and deliberately so: peers roam, and
    /// the app already migrates conversation history when a saved contact turns
    /// up on a new IP. A pinned key arriving from an unfamiliar address is the
    /// normal case of somebody changing network, not a warning.
    public static PeerKeyTrust Evaluate(
        string peerPublicKeyB64, string peerIP, IReadOnlyList<KnownContact> contacts)
    {
        if (string.IsNullOrEmpty(peerPublicKeyB64)) return PeerKeyTrust.Unknown;

        foreach (var contact in contacts)
        {
            if (contact.PublicKeyB64 == peerPublicKeyB64)
            {
                return PeerKeyTrust.Pinned(contact.Username);
            }
        }

        // An unfamiliar key at a familiar address. If several contacts have lived
        // here, the first is reported — the point is to raise the question, and
        // naming one previous occupant does that as well as naming three.
        if (!string.IsNullOrEmpty(peerIP))
        {
            foreach (var contact in contacts)
            {
                if (!string.IsNullOrEmpty(contact.LastIP) && contact.LastIP == peerIP)
                {
                    return PeerKeyTrust.ChangedAtKnownAddress(
                        contact.Username, contact.PublicKeyB64);
                }
            }
        }

        return PeerKeyTrust.Unknown;
    }
}
