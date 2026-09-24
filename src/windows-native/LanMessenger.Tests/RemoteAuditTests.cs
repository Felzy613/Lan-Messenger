using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Persistence;
using LanMessenger.UI;
using LanMessenger.UI.Chat;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using System.Text.Json;

namespace LanMessenger.Tests;

// The audit trail as it reaches the conversation. Mirror of
// RemoteAuditTests.swift; the record's own wording is covered in
// RemoteSessionShapeTests.
//
// A record is stored the way an attachment is: an ordinary history entry whose
// text is a marker and a JSON body. That leaves the history format unchanged,
// but every place that inspects message text has to know one more prefix. On
// Windows none of them did for a while, and nothing showed it, because nothing
// ever wrote a record: RemoteDesktopController.AppendAudit was declared, passed
// into every session, and never assigned. These tests cover the places that
// read the text: the history round trip, the sidebar preview, and the chat row.
[TestClass]
public class RemoteAuditTests
{
    private const string Peer = "192.168.99.79";
    private const double When = 1_700_000_000;

    [TestCleanup]
    public void Cleanup() => HistoryStore.Shared.Delete(Peer);

    private static RemoteAuditRecord Record(RemoteAuditEvent e, bool viewing = false) =>
        e == RemoteAuditEvent.SessionEnded
            ? new RemoteAuditRecord(e, "Dave Felzy", "user_stopped", 372, viewing)
            : new RemoteAuditRecord(e, "Dave Felzy", viewing: viewing);

    private static IEnumerable<RemoteAuditRecord> EveryRecord() =>
        from e in Enum.GetValues<RemoteAuditEvent>()
        from viewing in new[] { false, true }
        select Record(e, viewing);

    // MARK: - Storage

    [TestMethod]
    public void ARecordSurvivesARoundTripThroughHistory()
    {
        // Filed the way AppModel files one, then taken through the history
        // file's own format (the JSON HistoryStore.Save writes, sealed and
        // opened with the history key) and read back.
        var originals = EveryRecord().ToList();
        foreach (var record in originals)
            HistoryStore.Shared.Append(record.HistoryEntry(When), Peer);

        using var key = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, List<MessageEntry>> { [Peer] = HistoryStore.Shared.Entries(Peer) });
        var reloaded = JsonSerializer.Deserialize<Dictionary<string, List<MessageEntry>>>(
            HistoryCrypto.DecryptHistory(HistoryCrypto.EncryptHistory(plaintext, key), key))![Peer];

        Assert.AreEqual(originals.Count, reloaded.Count);
        for (int i = 0; i < originals.Count; i++)
        {
            var decoded = RemoteAuditRecord.Decode(reloaded[i].Text);
            Assert.IsNotNull(decoded, $"{originals[i].EventToken} did not survive history");
            Assert.AreEqual(originals[i].Encoded(), decoded!.Encoded());
            Assert.AreEqual(originals[i].Summary, decoded.Summary,
                "the sentence changed across storage, so the chair it was written from did too");
            Assert.AreEqual(When, reloaded[i].Timestamp);
        }
    }

    [TestMethod]
    public void TheStoredEntryIsNotAnUnreadMessage()
    {
        // Incoming entries drive the unread badge and the read-receipt path.
        // An audit record is a note about what happened, not something the peer
        // said, and a badge for it would be unexplainable.
        var entry = Record(RemoteAuditEvent.SessionStarted).HistoryEntry(When);
        Assert.IsFalse(entry.Incoming);
        Assert.IsTrue(entry.ReadReceiptSent);
        Assert.IsNull(entry.MessageId,
            "no message id: there is no packet it corresponds to, and edit_message "
            + "and delete_message both find their target by id");
        Assert.IsTrue(RemoteAuditRecord.IsAudit(entry.Text));
    }

    [TestMethod]
    public void TheStoredBytesMatchTheSwiftEncoder()
    {
        // Expected values printed by JSONEncoder with .sortedKeys, which is
        // what RemoteAuditEntry.encoded() uses. Declaration order put "event"
        // first, and the default escaper wrote the apostrophe as \u0027 and
        // the ë as \u00EB.
        Assert.AreEqual(
            """__REMOTE__:{"duration":372.5,"event":"session_ended","peerName":"Zoë's PC","reason":"user_stopped","viewing":true}""",
            new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, "Zoë's PC", "user_stopped", 372.5,
                                  viewing: true).Encoded());
        Assert.AreEqual(
            """__REMOTE__:{"event":"session_started","peerName":"Dave"}""",
            new RemoteAuditRecord(RemoteAuditEvent.SessionStarted, "Dave").Encoded());
    }

    // MARK: - The sidebar preview

    [TestMethod]
    public void ThePreviewShowsTheSentenceNotTheJson()
    {
        foreach (var record in EveryRecord())
        {
            var entries = new List<MessageEntry>
            {
                new() { Sender = "Dave Felzy", Text = "hello", Incoming = true, Timestamp = When - 60 },
                record.HistoryEntry(When),
            };
            string preview = AppModel.LastMessagePreview(entries);
            Assert.AreEqual(record.Summary, preview);
            Assert.IsFalse(preview.Contains(RemoteAuditRecord.Marker), preview);
            Assert.IsFalse(preview.Contains('{'), $"raw JSON in the sidebar: {preview}");
        }
    }

    [TestMethod]
    public void ThePreviewIsWrittenFromTheReadersChair()
    {
        var entries = new List<MessageEntry>
        {
            Record(RemoteAuditEvent.SessionStarted, viewing: true).HistoryEntry(When),
        };
        Assert.AreEqual("You started viewing Dave Felzy's screen.", AppModel.LastMessagePreview(entries));
    }

    [TestMethod]
    public void ARecordThisBuildCannotReadStillNeverShowsItsJson()
    {
        // A record from a newer build with an event this one does not know.
        // It is still an audit record, so it is still not a message body.
        var entry = new MessageEntry
        {
            Text = """__REMOTE__:{"event":"teleported","peerName":"Dave Felzy"}""",
            Timestamp = When, ReadReceiptSent = true,
        };
        Assert.AreEqual(RemoteAuditRecord.UndecodableSummary, AppModel.LastMessagePreview([entry]));

        var row = MessageRowViewModel.From([entry], 0);
        Assert.IsTrue(row.IsAudit, "an unreadable record fell through to a bubble");
        Assert.IsNull(row.Audit);
        Assert.AreEqual(RemoteAuditRecord.UndecodableSummary, row.Text);
    }

    // MARK: - The chat row

    [TestMethod]
    public void TheRowIsASystemLineThatCannotBeEditedRepliedToOrDeletedForEveryone()
    {
        foreach (var record in EveryRecord())
        {
            var row = MessageRowViewModel.From([record.HistoryEntry(When)], 0);
            Assert.IsTrue(row.IsAudit, "an audit record was built as a bubble");
            Assert.AreEqual(record.Event, row.Audit?.Event);
            Assert.AreEqual(record.Summary, row.Text);
            Assert.IsFalse(row.IsFile);
            Assert.IsFalse(row.IsEditable, "an audit row offered Edit");
            Assert.IsFalse(row.CanReply, "an audit row offered Reply");
            Assert.IsFalse(row.CanDeleteForEveryone, "an audit row offered Delete for Everyone");
        }
    }

    [TestMethod]
    public void TheModelRefusesToEditARecordEvenOneCarryingAnId()
    {
        // EditMessage asks IsEditable. A record has no id today, which is
        // already enough to refuse it; this asserts the refusal does not rest
        // on that alone, since rewriting the trail is the one thing it exists
        // to prevent.
        var entry = Record(RemoteAuditEvent.ControlGranted).HistoryEntry(When);
        entry.MessageId = "0123456789abcdef0123456789abcdef";
        Assert.IsFalse(AppModel.IsEditable(entry));
        Assert.IsFalse(MessageRowViewModel.From([entry], 0).IsEditable);
    }

    [TestMethod]
    public void AnOrdinaryMessageKeepsItsAffordances()
    {
        // The other half of the test above: the menu permissions moved onto the
        // row, and a predicate that refused everything would pass it.
        var entry = new MessageEntry
        {
            Sender = "me", Text = "hello", Incoming = false, Timestamp = When,
            MessageId = "0123456789abcdef0123456789abcdef", Status = "Sent",
        };
        var row = MessageRowViewModel.From([entry], 0);
        Assert.IsFalse(row.IsAudit);
        Assert.IsNull(row.Audit);
        Assert.AreEqual("hello", row.Text);
        Assert.IsTrue(AppModel.IsEditable(entry));
        Assert.IsTrue(row.IsEditable);
        Assert.IsTrue(row.CanReply);
        Assert.IsTrue(row.CanDeleteForEveryone);
    }
}
