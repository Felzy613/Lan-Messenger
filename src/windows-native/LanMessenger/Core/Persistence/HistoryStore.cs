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

    public void UpdateStatus(string status, string messageId, string peerIP)
    {
        if (!_history.TryGetValue(peerIP, out var list)) return;
        foreach (var e in list.Where(e => e.MessageId == messageId))
            e.Status = status;
    }

    public List<MessageEntry> Entries(string peerIP) =>
        _history.TryGetValue(peerIP, out var list) ? list : [];
}
