using LanMessenger.Core.Persistence;
using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace LanMessenger.Tests;

// Conversations are filed by identity key, not by LAN address. See PeerId.cs and
// PROTOCOL.md -> History.
//
// The migration is the part worth guarding: it runs once over history written
// under addresses, and the failure it exists to prevent — one person's messages
// shown as another's — is exactly what a careless guess at an ambiguous address
// would produce. So the tests are mostly about what it refuses to do.
// Mirror of PeerIDTests.swift; peer_id_vector.json is asserted by both.
[TestClass]
public class PeerIdTests
{
    // Real-shaped keys: base64 of 32 bytes.
    private static readonly string Dell = Convert.ToBase64String(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly string Ari  = Convert.ToBase64String(Enumerable.Repeat((byte)0x22, 32).ToArray());

    private static MessageEntry Entry(string? id, double t, string text = "x") => new()
    {
        Sender = "p", Text = text, Incoming = true, Timestamp = t, MessageId = id,
    };

    // MARK: - What an id is

    [TestMethod]
    public void AKeyIsAnIdAndAnAddressIsNot()
    {
        Assert.IsTrue(PeerId.IsKey(Dell));
        Assert.IsFalse(PeerId.IsKey("192.168.1.27"));
        Assert.IsFalse(PeerId.IsKey("ip:192.168.1.27"));
        // Base64, but not 32 bytes: not a key.
        Assert.IsFalse(PeerId.IsKey("AQIDBA=="));
        Assert.IsFalse(PeerId.IsKey(""));
        Assert.IsFalse(PeerId.IsKey(null));
    }

    [TestMethod]
    public void LegacyIdsRoundTripTheirAddress()
    {
        var id = PeerId.Legacy("192.168.1.27");
        Assert.AreEqual("ip:192.168.1.27", id);
        Assert.IsTrue(PeerId.IsLegacy(id));
        Assert.AreEqual("192.168.1.27", PeerId.LegacyAddress(id));
        Assert.IsNull(PeerId.LegacyAddress(Dell));
    }

    // MARK: - Resolving one name

    /// Two contacts recorded at one address is the DHCP collision itself.
    /// Picking either would hand one person's thread to the other.
    [TestMethod]
    public void AnAddressTwoContactsShareIsNotGuessed()
    {
        var contacts = new List<PeerId.Contact> { new(Dell, "192.168.1.27"), new(Ari, "192.168.1.27") };
        Assert.AreEqual("ip:192.168.1.27", PeerId.Resolve("192.168.1.27", contacts));
    }

    [TestMethod]
    public void AnAddressOwnedByExactlyOneContactBecomesTheirKey()
    {
        var contacts = new List<PeerId.Contact> { new(Dell, "192.168.1.27"), new(Ari, "192.168.1.31") };
        Assert.AreEqual(Dell, PeerId.Resolve("192.168.1.27", contacts));
        Assert.AreEqual(Ari, PeerId.Resolve("192.168.1.31", contacts));
    }

    // MARK: - Re-filing a whole history

    /// A bucket with one source is left exactly as it was — re-sorting it could
    /// swap two messages that share a timestamp.
    [TestMethod]
    public void ASingleBucketIsNotReordered()
    {
        var contacts = new List<PeerId.Contact> { new(Dell, "192.168.1.27") };
        var history = new Dictionary<string, List<MessageEntry>>
        {
            ["192.168.1.27"] = [Entry("late", 9), Entry("early", 1)],
        };
        var (result, _) = PeerId.Rekey(history, contacts, 200);
        CollectionAssert.AreEqual(new[] { "late", "early" }, result[Dell].Select(e => e.MessageId).ToList());
    }

    /// Messages sharing a timestamp keep the order they arrived in, and entries
    /// with no id (older attachments) are never de-duplicated away.
    [TestMethod]
    public void TheMergeIsStableAndKeepsIdlessEntries()
    {
        var merged = PeerId.Merge(
            [[Entry(null, 5, "first"), Entry("x", 5, "second")],
             [Entry(null, 5, "third"), Entry("x", 5, "dup")]], 200);
        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, merged.Select(e => e.Text).ToList());
    }

    /// Runs at every load, so a migrated history must pass through untouched and
    /// report nothing moved — otherwise every launch rewrites the file.
    [TestMethod]
    public void RekeyingIsIdempotent()
    {
        var contacts = new List<PeerId.Contact> { new(Dell, "192.168.1.27") };
        var history = new Dictionary<string, List<MessageEntry>>
        {
            ["192.168.1.27"] = [Entry("a", 1)],
            ["192.168.1.9"]  = [Entry("b", 2)],
        };
        var (once, _) = PeerId.Rekey(history, contacts, 200);
        var (twice, moved) = PeerId.Rekey(once, contacts, 200);
        Assert.AreEqual(0, moved.Count);
        CollectionAssert.AreEquivalent(once.Keys.ToList(), twice.Keys.ToList());
        Assert.AreEqual("b", twice["ip:192.168.1.9"].Single().MessageId);
    }

    [TestMethod]
    public void HistoryStoreMergesAPlaceholderIntoTheKey()
    {
        var placeholder = PeerId.RelayPlaceholder(Dell);
        try
        {
            HistoryStore.Shared.Append(Entry("relayed", 1), placeholder);
            HistoryStore.Shared.Append(Entry("lan", 2), Dell);

            Assert.IsTrue(HistoryStore.Shared.Merge(placeholder, Dell));
            CollectionAssert.AreEqual(new[] { "relayed", "lan" },
                HistoryStore.Shared.Entries(Dell).Select(e => e.MessageId).ToList());
            Assert.AreEqual(0, HistoryStore.Shared.Entries(placeholder).Count);
            Assert.IsFalse(HistoryStore.Shared.Merge(placeholder, Dell), "nothing left to move");
        }
        finally
        {
            HistoryStore.Shared.Delete(placeholder);
            HistoryStore.Shared.Delete(Dell);
        }
    }

    // MARK: - The shared vector

    private static JsonElement Vector()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "peer_id_vector.json");
        if (!File.Exists(path)) path = "peer_id_vector.json";
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }

    /// The same person reads both machines' histories, so the two platforms must
    /// file the same legacy bucket under the same id.
    [TestMethod]
    public void TheSharedVectorResolvesIdenticallyOnBothPlatforms()
    {
        var v = Vector();
        var contacts = v.GetProperty("contacts").EnumerateArray()
            .Select(c => new PeerId.Contact(c.GetProperty("public_key_b64").GetString()!,
                                            c.GetProperty("last_ip").GetString()!))
            .ToList();

        foreach (var c in v.GetProperty("is_key").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString()!;
            Assert.AreEqual(c.GetProperty("is_key").GetBoolean(), PeerId.IsKey(id), id);
        }
        foreach (var c in v.GetProperty("resolve").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString()!;
            Assert.AreEqual(c.GetProperty("id").GetString(), PeerId.Resolve(name, contacts), name);
        }
        var placeholder = v.GetProperty("relay_placeholder");
        Assert.AreEqual(placeholder.GetProperty("id").GetString(),
                        PeerId.RelayPlaceholder(placeholder.GetProperty("key").GetString()!));

        var rekey = v.GetProperty("rekey");
        var history = rekey.GetProperty("history").EnumerateObject().ToDictionary(
            b => b.Name,
            b => b.Value.EnumerateArray()
                  .Select(e => Entry(e.GetProperty("message_id").GetString(), e.GetProperty("timestamp").GetDouble()))
                  .ToList());
        var (result, moved) = PeerId.Rekey(history, contacts, rekey.GetProperty("cap").GetInt32());

        var expectedMoved = rekey.GetProperty("moved").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        CollectionAssert.AreEquivalent(expectedMoved.ToList(), moved.ToList());
        var expected = rekey.GetProperty("expected").EnumerateObject().ToList();
        Assert.AreEqual(expected.Count, result.Count);
        foreach (var bucket in expected)
            CollectionAssert.AreEqual(
                bucket.Value.EnumerateArray().Select(e => e.GetString()).ToList(),
                result[bucket.Name].Select(e => e.MessageId).ToList(), bucket.Name);

        var list = v.GetProperty("rekey_list");
        CollectionAssert.AreEqual(
            list.GetProperty("expected").EnumerateArray().Select(e => e.GetString()).ToList(),
            PeerId.Rekey(list.GetProperty("list").EnumerateArray().Select(e => e.GetString()!), contacts));
    }
}

// Unencrypted packets — typing, receipts, delete_message — only CLAIM a sender
// key. Filed by that claim alone they would need no connection from anywhere in
// particular, so a claim is accepted only from an address the key is known at.
[TestClass]
public class ClaimedSenderBindingTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)0x33, 32).ToArray());
    private Func<string, string, bool>? _saved;

    [TestInitialize]
    public void Bind()
    {
        _saved = MessagingService.Shared.IsBoundAddress;
        MessagingService.Shared.IsBoundAddress = (k, ip) => k == Key && ip == "192.168.1.27";
        HistoryStore.Shared.Append(new MessageEntry
        {
            Sender = "Dell", Text = "keep me", Incoming = true, Timestamp = 1, MessageId = "del-claim",
        }, Key);
    }

    [TestCleanup]
    public void Restore()
    {
        MessagingService.Shared.IsBoundAddress = _saved;
        HistoryStore.Shared.Delete(Key);
    }

    private static void DeleteFrom(string ip) =>
        MessagingService.Shared.HandlePacket(new ValidatedDelete(new ReceiptPacket
        {
            Type = "delete_message", MessageId = "del-claim", Sender = "Dell",
            SenderPublicKeyB64 = Key, Port = 54232,
        }, ip));

    /// Somebody else on the LAN naming the Dell's key: refused, not applied to
    /// the Dell's thread.
    [TestMethod]
    public void AnUnboundDeleteLeavesTheMessageAlone()
    {
        DeleteFrom("192.168.1.31");
        var stored = HistoryStore.Shared.Entries(Key).Single();
        Assert.AreEqual("keep me", stored.Text);
        Assert.IsFalse(stored.Deleted);
    }

    [TestMethod]
    public void ABoundDeleteIsFiledUnderTheKey()
    {
        DeleteFrom("192.168.1.27");
        Assert.IsTrue(HistoryStore.Shared.Entries(Key).Single().Deleted);
    }
}
