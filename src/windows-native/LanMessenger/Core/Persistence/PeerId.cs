namespace LanMessenger.Core.Persistence;

/// <summary>
/// What a conversation, its history bucket and its archived/hidden flag are
/// filed under: the peer's <b>identity key</b>, never its network address.
///
/// Every device already has a persistent identity — the X25519 public key its
/// contact entry is pinned by. Its LAN address is whatever DHCP handed it this
/// morning, and the same numbers are handed to other machines: over four days on
/// the network this was written on, one device used three addresses and another
/// used eight, one of them the first device's current address. Filing a
/// conversation by address therefore files it under a name that is later given
/// to somebody else, and every lookup that asks "who is at this conversation's
/// address?" eventually answers with the wrong device. On 2026-09-24 that sent a
/// remote-desktop invite to the wrong person.
///
/// A conversation id is one of:
/// <list type="bullet">
/// <item><b>a peer key</b> — base64 of a 32-byte X25519 public key. Every
/// conversation created from here on.</item>
/// <item><b><c>ip:&lt;address&gt;</c></b> — history written before identity keys
/// were the filing key, which no saved contact could be matched to at
/// migration. Kept, never dropped: it is still somebody's conversation, and a
/// guess would be worse.</item>
/// </list>
///
/// Mirror of <c>PeerID.swift</c>. Both platforms must produce the same id for
/// the same input, because the same person reads both machines' histories.
/// </summary>
public static class PeerId
{
    /// <summary>The prefix that marks history that could not be attributed to a key.</summary>
    public const string LegacyPrefix = "ip:";

    /// <summary>True for a base64 X25519 public key — the only thing a conversation id is, from now on.</summary>
    public static bool IsKey(string? id)
    {
        if (id is null || id.Length != 44 || !id.EndsWith('=')) return false;
        Span<byte> raw = stackalloc byte[33];
        return Convert.TryFromBase64String(id, raw, out var written) && written == 32;
    }

    /// <summary>True for history filed under an address that no key could be matched to.</summary>
    public static bool IsLegacy(string? id) =>
        id is not null && id.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    /// <summary>The id for a bucket that could not be attributed.</summary>
    public static string Legacy(string address) => LegacyPrefix + address;

    /// <summary>The address a legacy id was filed under, or null for a key.</summary>
    public static string? LegacyAddress(string? id) =>
        IsLegacy(id) ? id![LegacyPrefix.Length..] : null;

    /// <summary>
    /// The placeholder a relay message from <paramref name="key"/> was filed
    /// under by older builds, when it came from a peer never met on the LAN.
    /// Re-filed under the key once that peer is known: the relay copy was
    /// authenticated by decrypting under the key, so the prefix match is not a
    /// guess.
    /// </summary>
    public static string RelayPlaceholder(string key) =>
        Legacy("relay-" + key[..Math.Min(8, key.Length)]);

    // MARK: - Migration

    /// <summary>A saved contact, as far as migration needs one.</summary>
    public sealed record Contact(string PublicKeyB64, string LastIP);

    /// <summary>
    /// The conversation id a pre-migration bucket or list entry belongs to.
    /// A key is already an id; an <c>ip:</c> id is already migrated; an address
    /// owned by <b>exactly one</b> saved contact is that contact's;
    /// <c>relay-&lt;prefix&gt;</c> — the placeholder once used for a relay message
    /// from a peer never met on the LAN — is the contact whose key has that
    /// prefix, again only if exactly one does. Anything else is kept as
    /// <c>ip:&lt;address&gt;</c>.
    ///
    /// Ambiguity is never resolved by guessing. Two contacts filed under one
    /// address is precisely the collision this migration exists to end, and
    /// giving one person's messages to the other is the failure it prevents.
    /// </summary>
    public static string Resolve(string name, IReadOnlyList<Contact> contacts)
    {
        if (IsKey(name) || IsLegacy(name)) return name;

        if (name.StartsWith("relay-", StringComparison.Ordinal))
        {
            var prefix = name["relay-".Length..];
            var owners = contacts.Where(c => prefix.Length > 0
                && c.PublicKeyB64.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            return owners.Count == 1 ? owners[0].PublicKeyB64 : Legacy(name);
        }

        var byAddress = contacts.Where(c => c.LastIP == name && IsKey(c.PublicKeyB64)).ToList();
        return byAddress.Count == 1 ? byAddress[0].PublicKeyB64 : Legacy(name);
    }

    /// <summary>
    /// Re-files a whole history map under conversation ids. Pure. Buckets that
    /// land on the same id are merged: de-duplicated by message id, in timestamp
    /// order, capped. That is the ordinary case, not an edge — a contact that
    /// moved address had a bucket under each.
    /// </summary>
    public static (Dictionary<string, List<MessageEntry>> History, Dictionary<string, string> Moved)
        Rekey(IReadOnlyDictionary<string, List<MessageEntry>> history,
              IReadOnlyList<Contact> contacts, int cap)
    {
        var sources = new Dictionary<string, List<List<MessageEntry>>>();
        var moved = new Dictionary<string, string>();
        // Ordinal-sorted names, so which bucket's copy of a duplicated message
        // survives does not depend on dictionary order — and matches Swift's
        // sort, which also compares code units.
        foreach (var name in history.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var id = Resolve(name, contacts);
            if (id != name) moved[name] = id;
            if (!sources.TryGetValue(id, out var parts)) sources[id] = parts = [];
            parts.Add(history[name]);
        }

        var result = new Dictionary<string, List<MessageEntry>>();
        foreach (var (id, parts) in sources)
            // One source is already in order and within the cap. Re-sorting it
            // would risk swapping messages that share a timestamp.
            result[id] = parts.Count == 1 ? parts[0] : Merge(parts, cap);
        return (result, moved);
    }

    /// <summary>
    /// Joins several buckets that belong to one conversation: de-duplicated by
    /// message id (the first copy wins; entries without one are always kept),
    /// in timestamp order, capped to the newest <paramref name="cap"/>. Stable —
    /// messages sharing a timestamp keep the order they arrived in.
    /// </summary>
    public static List<MessageEntry> Merge(IEnumerable<IEnumerable<MessageEntry>> parts, int cap)
    {
        var seen = new HashSet<string>();
        // OrderBy is a stable sort, which is the property that matters here.
        var ordered = parts.SelectMany(p => p)
            .Where(e => e.MessageId is null || seen.Add(e.MessageId))
            .OrderBy(e => e.Timestamp)
            .ToList();
        return ordered.Count > cap ? ordered.TakeLast(cap).ToList() : ordered;
    }

    /// <summary>
    /// Re-files a list of conversation ids (archived, hidden). Pure; keeps order
    /// and drops duplicates the merge creates.
    /// </summary>
    public static List<string> Rekey(IEnumerable<string> list, IReadOnlyList<Contact> contacts)
    {
        var seen = new HashSet<string>();
        return list.Select(n => Resolve(n, contacts)).Where(seen.Add).ToList();
    }
}
