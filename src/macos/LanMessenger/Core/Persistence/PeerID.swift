import Foundation

/// What a conversation, its history bucket and its archived/hidden flag are
/// filed under: the peer's **identity key**, never its network address.
///
/// Every device already has a persistent identity — the X25519 public key its
/// contact entry is pinned by. Its LAN address is whatever DHCP handed it this
/// morning, and the same numbers are handed to other machines: over four days on
/// the network this was written on, one device used three addresses and another
/// used eight, one of them the first device's current address. Filing a
/// conversation by address therefore files it under a name that is later given
/// to somebody else, and every lookup that asks "who is at this conversation's
/// address?" eventually answers with the wrong device. On 2026-09-24 that sent a
/// remote-desktop invite to the wrong person and could have encrypted a message
/// to the wrong person's key.
///
/// A conversation id is one of:
///
/// - **a peer key** — base64 of a 32-byte X25519 public key. Every conversation
///   created from here on.
/// - **`ip:<address>`** — history written before identity keys were the filing
///   key, which no saved contact could be matched to at migration. Kept, never
///   dropped: it is still somebody's conversation, and a guess would be worse.
///
/// Mirror of `PeerId.cs`. Both platforms must produce the same id for the same
/// input, because the same person reads both machines' histories.
enum PeerID {

    /// The prefix that marks history that could not be attributed to a key.
    static let legacyPrefix = "ip:"

    /// True for a base64 X25519 public key — the only thing a conversation id
    /// is, from now on.
    static func isKey(_ id: String) -> Bool {
        guard id.count == 44, id.hasSuffix("="),
              let raw = Data(base64Encoded: id) else { return false }
        return raw.count == 32
    }

    /// True for history filed under an address that no key could be matched to.
    static func isLegacy(_ id: String) -> Bool { id.hasPrefix(legacyPrefix) }

    /// The id for a bucket that could not be attributed.
    static func legacy(address: String) -> String { legacyPrefix + address }

    /// The address a legacy id was filed under, or nil for a key.
    static func legacyAddress(_ id: String) -> String? {
        isLegacy(id) ? String(id.dropFirst(legacyPrefix.count)) : nil
    }

    // MARK: - Migration

    /// A saved contact, as far as migration needs one.
    struct Contact: Equatable {
        let publicKeyB64: String
        let lastIP: String
    }

    /// The conversation id a pre-migration bucket or list entry belongs to.
    ///
    /// - A key is already an id.
    /// - An `ip:` id is already migrated.
    /// - An address owned by **exactly one** saved contact is that contact's.
    /// - `relay-<prefix>` — the placeholder once used for a relay message from a
    ///   peer never met on the LAN — is the contact whose key has that prefix,
    ///   again only if exactly one does.
    /// - Anything else is kept as `ip:<address>`.
    ///
    /// Ambiguity is never resolved by guessing. Two contacts filed under one
    /// address is precisely the collision this migration exists to end, and
    /// giving one person's messages to the other is the failure it prevents.
    static func resolve(legacyName name: String, contacts: [Contact]) -> String {
        if isKey(name) || isLegacy(name) { return name }

        if name.hasPrefix("relay-") {
            let prefix = String(name.dropFirst("relay-".count))
            let owners = contacts.filter { !prefix.isEmpty && $0.publicKeyB64.hasPrefix(prefix) }
            if owners.count == 1 { return owners[0].publicKeyB64 }
            return legacy(address: name)
        }

        let owners = contacts.filter { $0.lastIP == name && isKey($0.publicKeyB64) }
        if owners.count == 1 { return owners[0].publicKeyB64 }
        return legacy(address: name)
    }

    /// Re-files a whole history map under conversation ids. Pure.
    ///
    /// Buckets that land on the same id are merged: de-duplicated by
    /// `messageId`, in timestamp order, capped. That is the ordinary case, not
    /// an edge — a contact that moved address had a bucket under each, and the
    /// old app merged them one move at a time.
    static func rekey(history: [String: [MessageEntry]],
                      contacts: [Contact],
                      cap: Int) -> (history: [String: [MessageEntry]], moved: [String: String]) {
        var sources: [String: [[MessageEntry]]] = [:]
        var moved: [String: String] = [:]
        // Sorted names, so which bucket's copy of a duplicated message survives
        // does not depend on dictionary order.
        for name in history.keys.sorted() {
            let id = resolve(legacyName: name, contacts: contacts)
            if id != name { moved[name] = id }
            sources[id, default: []].append(history[name] ?? [])
        }

        var result: [String: [MessageEntry]] = [:]
        for (id, parts) in sources {
            // One source is already in order and within the cap. Re-sorting it
            // would risk swapping messages that share a timestamp.
            result[id] = parts.count == 1 ? parts[0] : merge(parts, cap: cap)
        }
        return (result, moved)
    }

    /// Joins several buckets that belong to one conversation: de-duplicated by
    /// `messageId` (the first copy wins), in timestamp order, capped to the
    /// newest `cap`. Stable — messages sharing a timestamp keep the order they
    /// arrived in.
    static func merge(_ parts: [[MessageEntry]], cap: Int) -> [MessageEntry] {
        var seen = Set<String>()
        let unique = parts.joined().filter { e in
            guard let mid = e.messageId else { return true }
            return seen.insert(mid).inserted
        }
        let ordered = unique.enumerated()
            .sorted { ($0.element.timestamp, $0.offset) < ($1.element.timestamp, $1.offset) }
            .map(\.element)
        return Array(ordered.suffix(cap))
    }

    /// The placeholder a relay message from `key` was filed under by older
    /// builds, when it came from a peer never met on the LAN. Re-filed under
    /// the key once that peer is known: the relay copy was authenticated by
    /// decrypting under the key, so the prefix match is not a guess.
    static func relayPlaceholder(forKey key: String) -> String {
        legacy(address: "relay-\(key.prefix(8))")
    }

    /// Re-files a list of conversation ids (archived, hidden). Pure; keeps
    /// order and drops duplicates the merge creates.
    static func rekey(list: [String], contacts: [Contact]) -> [String] {
        var seen = Set<String>()
        return list.map { resolve(legacyName: $0, contacts: contacts) }
                   .filter { seen.insert($0).inserted }
    }
}
