using LanMessenger.Core.Crypto;
using LanMessenger.Core.Services;
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
    // True when the sender replaced this message's text after sending it.
    // Timestamp keeps the ORIGINAL send time so an edit doesn't move the
    // message in the thread; EditedAt records when the edit happened.
    [JsonPropertyName("edited")]            public bool   Edited           { get; set; }
    [JsonPropertyName("edited_at")]         public double? EditedAt        { get; set; }

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
// Inner JSON structure: { "<conversation id>": [MessageEntry, ...] }
//
// Keyed by conversation id — the peer's identity key (see PeerId). It used to be
// the peer's LAN address, which DHCP recycles between machines, so a thread could
// be filed under a name later given to somebody else. The file shape is
// unchanged; only what the names mean is, and history written under addresses
// is re-filed once, at load. Max 200 entries per peer.
public sealed class HistoryStore
{
    public static HistoryStore Shared { get; } = new();

    public const int MaxEntriesPerPeer = 200;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, List<MessageEntry>> _history = [];

    // Snapshot of loaded history (shallow copy — do not mutate keys).
    public IReadOnlyDictionary<string, List<MessageEntry>> History => _history;

    private readonly string _path;

    private HistoryStore()
    {
        _path = RunningUnderTests
            ? Path.Combine(Path.GetTempPath(), $"lanmessenger-tests-{Environment.ProcessId}-history.enc")
            : ConfigStore.Shared.HistoryFilePath;
        Load();
    }

    /// <summary>
    /// The suite exercises this singleton directly, on the same machine and
    /// account as the real app — so without this the test host decrypted the
    /// user's own history, and any path that saved wrote test conversations
    /// back into it. On macOS one such thread, dated 1970, sat in a real
    /// history file until the user hid it. MSTest is never loaded by the app,
    /// so its presence is an unambiguous signal.
    /// </summary>
    private static bool RunningUnderTests => AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.GetName().Name == "Microsoft.VisualStudio.TestPlatform.TestFramework");

    // MARK: - Load

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var fileJson = File.ReadAllText(_path);
            var plaintext = HistoryCrypto.DecryptHistory(fileJson, KeyManager.Shared.PrivateKey);
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<MessageEntry>>>(plaintext);
            var capped = raw?.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.TakeLast(MaxEntriesPerPeer).ToList()
            ) ?? [];

            // Re-file anything still named by address. Idempotent: a key is
            // already an id, so a history that has been migrated passes through
            // untouched and nothing is written.
            var contacts = ConfigStore.Shared.Config.Contacts
                .Select(c => new PeerId.Contact(c.PublicKeyB64, c.LastIP)).ToList();
            var (rekeyed, moved) = PeerId.Rekey(capped, contacts, MaxEntriesPerPeer);
            _history = rekeyed;
            if (moved.Count > 0)
            {
                var unattributed = moved.Values.Count(PeerId.IsLegacy);
                LanLogger.Info("History",
                    $"re-filed {moved.Count} conversation(s) from address to identity key"
                    + (unattributed > 0 ? $" ({unattributed} kept under their address: no single contact owned it)" : ""));
                Save();
            }
        }
        catch { _history = []; }
    }

    // MARK: - Save

    // Latest un-flushed snapshot, exchanged atomically. The snapshot is taken
    // on the CALLING thread (the same thread that mutates _history), then
    // encrypted and written in the background. The previous implementation
    // serialized the live lists on a background thread — any message appended
    // mid-serialization threw "Collection was modified", the catch swallowed
    // it, and the save was silently lost.
    private byte[]? _dirtySnapshot;

    public Task SaveAsync()
    {
        Save();
        return FlushAsync();
    }

    // Snapshot on the caller's thread, flush in the background. Concurrent
    // Save() bursts (a receipt storm) coalesce: each flush writes only the
    // newest snapshot and earlier ones are skipped.
    public void Save()
    {
        byte[] snapshot;
        try
        {
            var trimmed = _history.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.TakeLast(MaxEntriesPerPeer).ToList()
            );
            snapshot = JsonSerializer.SerializeToUtf8Bytes(trimmed);
        }
        catch { return; }
        Interlocked.Exchange(ref _dirtySnapshot, snapshot);
        Task.Run(FlushAsync);
    }

    private async Task FlushAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = Interlocked.Exchange(ref _dirtySnapshot, null);
            if (snapshot is null) return;   // a newer flush already wrote it
            var fileJson = HistoryCrypto.EncryptHistory(snapshot, KeyManager.Shared.PrivateKey);
            var path = _path;
            // Write-to-temp + atomic replace so a crash mid-write can't leave a
            // truncated (undecryptable) history file behind.
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, fileJson).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
        finally { _lock.Release(); }
    }

    // MARK: - Mutations (call from UI thread)

    // Every `peer` below is a conversation id (PeerId) — the peer's identity key.

    public void Append(MessageEntry entry, string peer)
    {
        if (!_history.TryGetValue(peer, out var list))
            list = _history[peer] = [];
        list.Add(entry);
        if (list.Count > MaxEntriesPerPeer)
            _history[peer] = list.TakeLast(MaxEntriesPerPeer).ToList();
    }

    /// <summary>
    /// Moves everything filed under <paramref name="source"/> into
    /// <paramref name="target"/>, merged as <see cref="PeerId.Merge"/> does.
    /// Returns false when there was nothing to move. Caller persists.
    /// </summary>
    public bool Merge(string source, string target)
    {
        if (source == target || !_history.Remove(source, out var moving)) return false;
        var existing = _history.TryGetValue(target, out var cur) ? cur : [];
        _history[target] = PeerId.Merge([existing, moving], MaxEntriesPerPeer);
        return true;
    }

    public void MarkReadReceiptSent(string messageId, string peer)
    {
        if (!_history.TryGetValue(peer, out var list)) return;
        foreach (var e in list.Where(e => e.MessageId == messageId))
            e.ReadReceiptSent = true;
    }

    // Marks every incoming entry as read regardless of whether it has a MessageId.
    // File-transfer entries (MessageId == null) are not handled by MarkReadReceiptSent
    // and would otherwise remain unread after an app restart.
    public void MarkAllIncomingRead(string peer)
    {
        if (!_history.TryGetValue(peer, out var list)) return;
        foreach (var e in list.Where(e => e.Incoming && !e.ReadReceiptSent))
            e.ReadReceiptSent = true;
    }

    // Marks a message entry as having transited the cloud relay. Called once
    // the Worker has *confirmed* an outgoing message was stored (see
    // MessagingService.MarkRelayStored). Scans every bucket rather than
    // taking a conversation id — an outgoing message's bucket is known at send
    // time, but retries of a failed store (fired from the relay-outbox retry
    // loop, which only knows the messageId) need to find it without
    // re-resolving the conversation from state that may have changed since.
    public void MarkRelayDelivery(string messageId)
    {
        foreach (var list in _history.Values)
        {
            var entry = list.FirstOrDefault(e => e.MessageId == messageId);
            if (entry is null) continue;
            entry.DeliveryPath = "relay";
            return;
        }
    }

    // Scans every bucket, not just one conversation. The same message may have
    // arrived over the LAN already, and history written before conversations
    // were filed by key can hold it under an address-named bucket this key's
    // thread never reads. A per-conversation check misses that; this doesn't.
    public bool ContainsMessageId(string messageId) =>
        _history.Values.Any(list => list.Any(e => e.MessageId == messageId));

    // Rank-aware status update — never downgrades a delivered/read message back
    // to "Sent". Without this guard, the late "Sent" dispatch from the sender's
    // TCP-write completion would frequently overwrite the "Delivered" status
    // set by the receiver's sent_receipt, leaving the user with a single
    // check mark forever on cross-platform exchanges.
    //
    // Returns true if the status was actually applied (so callers know whether
    // to fire OnStatusUpdate and persist to disk).
    public bool UpdateStatus(string status, string messageId, string peer)
    {
        if (!_history.TryGetValue(peer, out var list)) return false;
        var applied = false;
        foreach (var e in list.Where(e => e.MessageId == messageId))
        {
            if (!MessageStatus.ShouldApply(status, e.Status)) continue;
            e.Status = status;
            applied = true;
        }
        return applied;
    }

    public List<MessageEntry> Entries(string peer) =>
        _history.TryGetValue(peer, out var list) ? list : [];

    // Marks a message as deleted: clears its text and reply preview fields and
    // sets Deleted = true, leaving a "this message was deleted" placeholder.
    // Caller is responsible for persisting via Save().
    /// <summary>
    /// <paramref name="requireIncoming"/> is the same security gate ApplyEdit
    /// uses, and for the same reason: a peer knows the message_id of everything
    /// we sent them, so an inbound delete naming one of OUR outgoing messages
    /// must be refused rather than allowed to blank what we said. Inbound
    /// notices pass true; our own "delete for everyone" passes false.
    /// </summary>
    public bool MarkDeleted(string messageId, string peer, bool requireIncoming)
    {
        if (!_history.TryGetValue(peer, out var list)) return false;
        var changed = false;
        foreach (var e in list.Where(e => e.MessageId == messageId && e.Incoming == requireIncoming))
        {
            changed            = true;
            e.Deleted          = true;
            e.Text             = "";
            e.ReplyToMessageId = null;
            e.ReplyToPreview   = null;
            e.ReplyToSender    = null;
        }
        return changed;
    }

    /// <summary>
    /// Replaces the text of the entry identified by <paramref name="messageId"/>,
    /// marking it edited. Returns true when an entry was actually changed.
    /// Caller is responsible for persisting via Save().
    ///
    /// <paramref name="requireIncoming"/> is the security gate, and it is not
    /// optional: a peer knows the message_id of every message we ever sent
    /// them, so an inbound edit_message naming one of OUR outgoing messages
    /// must be refused rather than allowed to rewrite what we said. Inbound
    /// edits pass true; our own edits of our own messages pass false.
    ///
    /// Attachments and already-deleted messages are never editable — a
    /// "__FILE__:" text is a local path, not a body the peer can replace.
    /// </summary>
    public bool ApplyEdit(string messageId, string peer, string newText, double editedAt, bool requireIncoming)
    {
        if (!_history.TryGetValue(peer, out var list)) return false;
        var changed = false;
        foreach (var e in list.Where(e => e.MessageId == messageId))
        {
            if (e.Incoming != requireIncoming) continue;
            if (e.Deleted) continue;
            if (e.Text.StartsWith("__FILE__:", StringComparison.Ordinal)) continue;
            e.Text     = newText;
            e.Edited   = true;
            e.EditedAt = editedAt;
            changed = true;
        }
        return changed;
    }

    // Removes the first entry matching `matching` (local-only "delete for me").
    // Caller is responsible for persisting via Save().
    public void RemoveEntry(MessageEntry matching, string peer)
    {
        if (!_history.TryGetValue(peer, out var list)) return;
        var idx = list.FindIndex(e => MessageEntry.SameEntry(e, matching));
        if (idx >= 0) list.RemoveAt(idx);
    }

    // Drops all messages for a conversation. Caller is responsible for persisting via Save().
    public void Delete(string peer) => _history.Remove(peer);
}
