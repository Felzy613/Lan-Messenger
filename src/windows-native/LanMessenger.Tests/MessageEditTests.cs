using LanMessenger.Core.Persistence;
using LanMessenger.Core.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace LanMessenger.Tests;

// Guards message editing. See PROTOCOL.md -> edit_message.
//
// The rule worth the most attention is requireIncoming. A peer knows the
// message_id of every message we ever sent them — we put it in the packet — so
// an inbound edit_message naming one of OUR outgoing messages would let them
// rewrite what we said in our own transcript. That is the one failure here with
// real consequences, and it is invisible unless something asserts it.
[TestClass]
public class MessageEditTests
{
    private const string Peer = "192.168.99.78";

    [TestCleanup]
    public void Cleanup() => HistoryStore.Shared.Delete(Peer);

    private static MessageEntry Outgoing(string id, string text) => new()
    {
        Sender = "me", Text = text, Incoming = false,
        Timestamp = 100, MessageId = id, Status = "Sent",
    };

    private static MessageEntry Incoming(string id, string text) => new()
    {
        Sender = "Peer", Text = text, Incoming = true,
        Timestamp = 100, MessageId = id, Status = "",
    };

    private static MessageEntry? Stored(string id) =>
        HistoryStore.Shared.History.TryGetValue(Peer, out var list)
            ? list.FirstOrDefault(e => e.MessageId == id)
            : null;

    // MARK: - The happy paths

    [TestMethod]
    public void SenderEditsOwnOutgoingMessage()
    {
        HistoryStore.Shared.Append(Outgoing("edit-own", "teh original"), Peer);

        Assert.IsTrue(HistoryStore.Shared.ApplyEdit("edit-own", Peer, "the original", 555, requireIncoming: false));

        var e = Stored("edit-own");
        Assert.AreEqual("the original", e!.Text);
        Assert.IsTrue(e.Edited);
        Assert.AreEqual(555, e.EditedAt);
        // The original send time must survive, or the message jumps position in
        // the thread the moment it is edited.
        Assert.AreEqual(100, e.Timestamp);
    }

    [TestMethod]
    public void InboundEditRewritesThePeersOwnMessage()
    {
        HistoryStore.Shared.Append(Incoming("edit-theirs", "hlelo"), Peer);

        Assert.IsTrue(HistoryStore.Shared.ApplyEdit("edit-theirs", Peer, "hello", 556, requireIncoming: true));

        Assert.AreEqual("hello", Stored("edit-theirs")!.Text);
        Assert.IsTrue(Stored("edit-theirs")!.Edited);
    }

    // MARK: - The security gate

    [TestMethod]
    public void InboundEditCannotRewriteOurOwnOutgoingMessage()
    {
        HistoryStore.Shared.Append(Outgoing("edit-spoof", "I agree to the terms"), Peer);

        // Exactly what a malicious peer would send: they know this id, because
        // we sent it to them.
        Assert.IsFalse(HistoryStore.Shared.ApplyEdit(
            "edit-spoof", Peer, "I agree to pay $10,000", 557, requireIncoming: true));

        var e = Stored("edit-spoof");
        Assert.AreEqual("I agree to the terms", e!.Text, "an inbound edit must never touch our own message");
        Assert.IsFalse(e.Edited);
    }

    [TestMethod]
    public void LocalEditCannotRewriteAMessageWeReceived()
    {
        HistoryStore.Shared.Append(Incoming("edit-inbound", "what they said"), Peer);

        Assert.IsFalse(HistoryStore.Shared.ApplyEdit(
            "edit-inbound", Peer, "what I wish they said", 558, requireIncoming: false));

        Assert.AreEqual("what they said", Stored("edit-inbound")!.Text);
    }

    // MARK: - What is not editable

    // An attachment's Text is a local filesystem path, not a message body.
    [TestMethod]
    public void AttachmentsAreNotEditable()
    {
        HistoryStore.Shared.Append(Outgoing("edit-file", @"__FILE__:C:\Users\dave\Downloads\report.pdf"), Peer);

        Assert.IsFalse(HistoryStore.Shared.ApplyEdit(
            "edit-file", Peer, "something else", 559, requireIncoming: false));

        Assert.AreEqual(@"__FILE__:C:\Users\dave\Downloads\report.pdf", Stored("edit-file")!.Text);
    }

    [TestMethod]
    public void DeletedMessagesAreNotEditable()
    {
        var entry = Outgoing("edit-deleted", "");
        entry.Deleted = true;
        HistoryStore.Shared.Append(entry, Peer);

        Assert.IsFalse(HistoryStore.Shared.ApplyEdit(
            "edit-deleted", Peer, "undelete me", 560, requireIncoming: false));

        Assert.AreEqual("", Stored("edit-deleted")!.Text);
        Assert.IsTrue(Stored("edit-deleted")!.Deleted);
    }

    [TestMethod]
    public void UnknownMessageIdChangesNothing()
    {
        HistoryStore.Shared.Append(Outgoing("edit-present", "untouched"), Peer);

        Assert.IsFalse(HistoryStore.Shared.ApplyEdit(
            "edit-absent", Peer, "ghost", 561, requireIncoming: false));

        Assert.AreEqual("untouched", Stored("edit-present")!.Text);
    }

    [TestMethod]
    public void UnknownPeerChangesNothing()
    {
        Assert.IsFalse(HistoryStore.Shared.ApplyEdit(
            "anything", "10.255.255.254", "ghost", 562, requireIncoming: true));
    }

    // MARK: - History wire format

    // Older history files predate both fields and must still load.
    [TestMethod]
    public void HistoryEntryDeserializesWithoutEditFields()
    {
        const string json = """
        {"sender":"Alice","text":"Hello","incoming":true,"timestamp":1715000000.0,
         "message_id":"abc","status":"","read_receipt_sent":false}
        """;

        var entry = JsonSerializer.Deserialize<MessageEntry>(json)!;
        Assert.IsFalse(entry.Edited);
        Assert.IsNull(entry.EditedAt);
    }

    [TestMethod]
    public void HistoryEntryRoundTripsEditFields()
    {
        var entry = new MessageEntry
        {
            Sender = "me", Text = "fixed", Incoming = false, Timestamp = 1,
            MessageId = "rt", Status = "Sent", Edited = true, EditedAt = 42,
        };
        var round = JsonSerializer.Deserialize<MessageEntry>(JsonSerializer.Serialize(entry))!;
        Assert.IsTrue(round.Edited);
        Assert.AreEqual(42, round.EditedAt);
    }

    // Cross-platform field naming: macOS writes edited_at, so Windows must too.
    [TestMethod]
    public void EditFieldsUseSnakeCaseOnTheWire()
    {
        var entry = new MessageEntry
        {
            Sender = "me", Text = "x", Incoming = false, Timestamp = 1,
            MessageId = "sc", Status = "", Edited = true, EditedAt = 7,
        };
        var json = JsonSerializer.Serialize(entry);
        StringAssert.Contains(json, "\"edited_at\"");
    }

    // MARK: - Packet validation

    private const string OwnKey  = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string PeerKey = "AQIDBA==";

    private static byte[] EditPacket(string messageId = "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
                                     int nonceBytes = 12)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"]                  = "edit_message",
            ["message_id"]            = messageId,
            ["timestamp"]             = 1715000123.456,
            ["sender"]                = "Alice",
            ["sender_public_key_b64"] = PeerKey,
            ["port"]                  = 54232,
            ["nonce"]                 = Convert.ToBase64String(new byte[nonceBytes]),
            ["ciphertext"]            = Convert.ToBase64String(new byte[48]),
        });
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    [TestMethod]
    public void ValidEditPacketParses()
    {
        var result = PacketValidator.Validate(EditPacket(), "1.2.3.4", OwnKey);
        var edit = result as ValidatedEdit;
        Assert.IsNotNull(edit, "expected ValidatedEdit");
        Assert.AreEqual("a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6", edit!.Packet.MessageId);
        Assert.AreEqual("1.2.3.4", edit.SenderIP);
    }

    // Same 12-byte rule as `text` — the AES-GCM nonce size is fixed.
    [TestMethod]
    public void EditPacketRejectsWrongNonceLength()
    {
        Assert.IsNull(PacketValidator.Validate(EditPacket(nonceBytes: 16), "1.2.3.4", OwnKey));
    }

    [TestMethod]
    public void EditPacketRejectsEmptyMessageId()
    {
        Assert.IsNull(PacketValidator.Validate(EditPacket(messageId: ""), "1.2.3.4", OwnKey));
    }

    [TestMethod]
    public void EditPacketFromSelfIsDropped()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"]                  = "edit_message",
            ["message_id"]            = "a3f1b2c4d5e6f7a8b9c0d1e2f3a4b5c6",
            ["timestamp"]             = 1.0,
            ["sender"]                = "Me",
            ["sender_public_key_b64"] = OwnKey,
            ["port"]                  = 54232,
            ["nonce"]                 = Convert.ToBase64String(new byte[12]),
            ["ciphertext"]            = Convert.ToBase64String(new byte[48]),
        });
        Assert.IsNull(PacketValidator.Validate(System.Text.Encoding.UTF8.GetBytes(json), "1.2.3.4", OwnKey));
    }
}
