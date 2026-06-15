using LanMessenger.Core.Crypto;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanMessenger.Core.Persistence;

public sealed class MessageEntry
{
    [JsonPropertyName("sender")]            public string Sender           { get; set; } = "";
    [JsonPropertyName("text")]              public string Text             { get; set; } = "";
    [JsonPropertyName("incoming")]          public bool   Incoming         { get; set; }
    [JsonPropertyName("timestamp")]         public double Timestamp        { get; set; }
    [JsonPropertyName("message_id")]        public string? MessageId       { get; set; }
    [JsonPropertyName("status")]            public string Status           { get; set; } = "";
    [JsonPropertyName("read_receipt_sent")] public bool   ReadReceiptSent  { get; set; }
    // Local-only reply metadata. Optional — older history files load fine.
    [JsonPropertyName("reply_to_message_id")] public string? ReplyToMessageId { get; set; }
    [JsonPropertyName("reply_to_preview")]    public string? ReplyToPreview   { get; set; }
    [JsonPropertyName("reply_to_sender")]     public string? ReplyToSender    { get; set; }
    // "relay" when this message transited the cloud relay Worker; null for direct LAN delivery.
    [JsonPropertyName("delivery_path")]     public string? DeliveryPath    { get; set; }
    // True when this message was deleted (locally or via delete_message). Text and
    // reply preview fields are cleared; the UI renders a "deleted" placeholder.
    [JsonPropertyName("deleted")]           public bool   Deleted          { get; set; }

    // Identity comparison used by deletion/removal — prefer MessageId when both
    // entries have one, otherwise fall back to timestamp+sender+text+direction.
    public static bool SameEntry(MessageEntry a, MessageEntry b)
    {
        if (!string.IsNullOrEmpty(a.MessageId) && !string.IsNullOrEmpty(b.MessageId))
            return a.MessageId == b.MessageId;
        return a.Timestamp == b.Timestamp && a.Sender == b.Sender
            && a.Text == b.Text && a.Incoming == b.Incoming;
    }
}

// Manages reading/writing the encrypted history file.
// Inner JSON structure: { "<peer_ip>": [MessageEntry, ...] } — keyed by peer IP (same as Python).
// Max 200 entries per peer.
public sealed class HistoryStore
{
    public static HistoryStore Shared { get; } = new();

    public const int MaxEntriesPerPeer = 200;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, List<MessageEntry>> _history = [];

    // Snapshot of loaded history (shallow copy — do not mutate keys).
    public IReadOnlyDictionary<string, List<MessageEntry>> History => _history;

    private HistoryStore() => Load();

    // MARK: - Load

    private void Load()
    {
        var path = ConfigStore.Shared.HistoryFilePath;
        if (!File.Exists(path)) return;
        try
        {
            var fileJson = File.ReadAllText(path);
            var plaintext = HistoryCrypto.DecryptHistory(fileJson, KeyManager.Shared.PrivateKey);
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<MessageEntry>>>(plaintext);
            _history = raw?.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.TakeLast(MaxEntriesPerPeer).ToList()
            ) ?? [];
        }
        catch { _history = []; }
    }

    // MARK: - Save

    public async Task SaveAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var trimmed = _history.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.TakeLast(MaxEntriesPerPeer).ToList()
            );
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(trimmed);
            var fileJson = HistoryCrypto.EncryptHistory(plaintext, KeyManager.Shared.PrivateKey);
            await File.WriteAllTextAsync(ConfigStore.Shared.HistoryFilePath, fileJson).ConfigureAwait(false);
        }
        catch { }
        finally { _lock.Release(); }
    }

    // Synchronous save for callers on the UI thread (fire-and-forget via Task.Run).
    public void Save() => Task.Run(SaveAsync);

    // MARK: - Mutations (call from UI thread)

    public void Append(MessageEntry entry, string peerIP)
    {
        if (!_history.TryGetValue(peerIP, out var list))
            list = _history[peerIP] = [];
        list.Add(entry);
        if (list.Count > MaxEntriesPerPeer)
            _history[peerIP] = list.TakeLast(MaxEntriesPerPeer).ToList();
    }

    public void MarkReadReceiptSent(string messageId, string peerIP)
    {
        if (!_history.TryGetValue(peerIP, out var list)) return;
        foreach (var e in list.Where(e => e.MessageId == messageId))
            e.ReadReceiptSent = true;
    }

    // Marks every incoming entry as read regardless of whether it has a MessageId.
    // File-transfer entries (MessageId == null) are not handled by MarkReadReceiptSent
    // and would otherwise remain unread after an app restart.
    public void MarkAllIncomingRead(string peerIP)
    {
        if (!_history.TryGetValue(peerIP, out var list)) return;
        foreach (var e in list.Where(e => e.Incoming && !e.ReadReceiptSent))
            e.ReadReceiptSent = true;
    }

    // Marks a message entry as having transited the cloud relay.
    // Called for outgoing messages when TCP fails and relay upload is attempted,
    // and for incoming messages when they arrive via relay.
    public void MarkRelayDelivery(string messageId, string peerIP)
    {
        if (!_history.TryGetValue(peerIP, out var list)) return;
        foreach (var e in list.Where(e => e.MessageId == messageId && e.DeliveryPath != "relay"))
            e.DeliveryPath = "relay";
    }

    // Rank-aware status update — never downgrades a delivered/read message back
    // to "Sent". Without this guard, the late "Sent" dispatch from the sender's
    // TCP-write completion would frequently overwrite the "Delivered" status
    // set by the receiver's sent_receipt, leaving the user with a single
    // check mark forever on cross-platform exchanges.
    //
    // Returns true if the status was actually applied (so callers know whether
    // to fire OnStatusUpdate and persist to disk).
    public bool UpdateStatus(string status, string messageId, string peerIP)
    {
        if (!_history.TryGetValue(peerIP, out var list)) return false;
        var applied = false;
        foreach (var e in list.Where(e => e.MessageId == messageId))
        {
            if (!MessageStatus.ShouldApply(status, e.Status)) continue;
            e.Status = status;
            applied = true;
        }
        return applied;
    }

    public List<MessageEntry> Entries(string peerIP) =>
        _history.TryGetValue(peerIP, out var list) ? list : [];

    // Marks a message as deleted: clears its text and reply preview fields and
    // sets Deleted = true, leaving a "this message was deleted" placeholder.
    // Caller is responsible for persisting via Save().
    public void MarkDeleted(string messageId, string peerIP)
    {
        if (!_history.TryGetValue(peerIP, out var list)) return;
        foreach (var e in list.Where(e => e.MessageId == messageId))
        {
            e.Deleted          = true;
            e.Text             = "";
            e.ReplyToMessageId = null;
            e.ReplyToPreview   = null;
            e.ReplyToSender    = null;
        }
    }

    // Removes the first entry matching `matching` (local-only "delete for me").
    // Caller is responsible for persisting via Save().
    public void RemoveEntry(MessageEntry matching, string peerIP)
    {
        if (!_history.TryGetValue(peerIP, out var list)) return;
        var idx = list.FindIndex(e => MessageEntry.SameEntry(e, matching));
        if (idx >= 0) list.RemoveAt(idx);
    }

    // Drops all messages for a peer IP. Caller is responsible for persisting via Save().
    public void Delete(string peerIP) => _history.Remove(peerIP);

    // Moves history from one peer IP to another (used when a saved contact reappears
    // on a different LAN IP). Entries are merged and re-sorted by timestamp.
    public void Migrate(string fromIP, string toIP)
    {
        if (fromIP == toIP) return;
        if (!_history.TryGetValue(fromIP, out var old)) return;
        _history.Remove(fromIP);
        var existing = _history.TryGetValue(toIP, out var cur) ? cur : [];
        var merged = existing.Concat(old).OrderBy(e => e.Timestamp).ToList();
        if (merged.Count > MaxEntriesPerPeer)
            merged = merged.TakeLast(MaxEntriesPerPeer).ToList();
        _history[toIP] = merged;
    }
}
