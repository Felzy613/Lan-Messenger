using LanMessenger.Core.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Guards the race-condition fix that caused the "single check mark" symptom
// in cross-platform Mac↔Windows messaging. See MessageStatus.cs.
[TestClass]
public class MessageStatusTests
{
    [TestMethod]
    public void RankOrderIsMonotonic()
    {
        Assert.IsTrue(MessageStatus.Rank(MessageStatus.Read)      > MessageStatus.Rank(MessageStatus.Delivered));
        Assert.IsTrue(MessageStatus.Rank(MessageStatus.Delivered) > MessageStatus.Rank(MessageStatus.Sent));
        Assert.IsTrue(MessageStatus.Rank(MessageStatus.Sent)      > MessageStatus.Rank(MessageStatus.Queued));
        Assert.IsTrue(MessageStatus.Rank(MessageStatus.Sent)      > MessageStatus.Rank(MessageStatus.Sending));
        Assert.IsTrue(MessageStatus.Rank(MessageStatus.Sent)      > MessageStatus.Rank(""));
        Assert.IsTrue(MessageStatus.Rank(MessageStatus.Sending)   > MessageStatus.Rank(MessageStatus.Failed));
    }

    [TestMethod]
    public void DeliveredCannotRegressToSent()
    {
        // The exact race scenario: the receipt arrives on the UI thread BEFORE
        // the sender's own "Sent" dispatch from the TCP-write completion. The
        // late "Sent" must not overwrite "Delivered".
        Assert.IsFalse(MessageStatus.ShouldApply(MessageStatus.Sent, MessageStatus.Delivered));
        Assert.IsFalse(MessageStatus.ShouldApply(MessageStatus.Sent, MessageStatus.Read));
    }

    [TestMethod]
    public void ReadCannotRegressToDelivered()
    {
        // Mirror of the Read/Delivered guard that was previously hand-rolled
        // in HandleReceipt — now enforced uniformly.
        Assert.IsFalse(MessageStatus.ShouldApply(MessageStatus.Delivered, MessageStatus.Read));
    }

    [TestMethod]
    public void UpgradesAreAllowed()
    {
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Sent,      ""));
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Sent,      MessageStatus.Sending));
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Sent,      MessageStatus.Queued));
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Delivered, MessageStatus.Sent));
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Read,      MessageStatus.Delivered));
    }

    [TestMethod]
    public void SameRankReapplyAllowed()
    {
        // Same-rank transitions are allowed (e.g. Sending → Queued) so the
        // UI can still reflect retry attempts.
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Queued, MessageStatus.Sending));
        Assert.IsTrue(MessageStatus.ShouldApply(MessageStatus.Read,   MessageStatus.Read));
    }

    [TestMethod]
    public void FailedDoesNotOverwriteDelivered()
    {
        // "Failed" only ever fires before the wire send begins, so even if a
        // pathological code path tried to apply it later, it must not regress
        // an already-delivered message.
        Assert.IsFalse(MessageStatus.ShouldApply(MessageStatus.Failed, MessageStatus.Delivered));
        Assert.IsFalse(MessageStatus.ShouldApply(MessageStatus.Failed, MessageStatus.Sent));
    }

    // MARK: - HistoryStore integration

    [TestMethod]
    public void HistoryStoreUpdateStatusIsRankAware()
    {
        var entry = new MessageEntry
        {
            Sender = "me", Text = "hi", Incoming = false,
            Timestamp = 1.0, MessageId = "msg-rank-test",
            Status = MessageStatus.Sending,
        };
        var peer = "192.168.99.99";
        HistoryStore.Shared.Append(entry, peer);

        // Simulate the race: receipt arrives first (Delivered), then the late
        // "Sent" dispatch from the sender's own send-completion.
        Assert.IsTrue(HistoryStore.Shared.UpdateStatus(MessageStatus.Delivered, "msg-rank-test", peer));
        Assert.IsFalse(HistoryStore.Shared.UpdateStatus(MessageStatus.Sent,      "msg-rank-test", peer));

        var stored = HistoryStore.Shared.Entries(peer).Single(e => e.MessageId == "msg-rank-test");
        Assert.AreEqual(MessageStatus.Delivered, stored.Status,
            "Late 'Sent' dispatch must not overwrite 'Delivered' — this was the single-tick bug.");

        // Cleanup
        HistoryStore.Shared.Delete(peer);
    }
}
