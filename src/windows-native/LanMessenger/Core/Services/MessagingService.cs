using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking;
using LanMessenger.Core.Persistence;
using LanMessenger.Core.Protocol;
using Microsoft.UI.Dispatching;
using System.Text;

namespace LanMessenger.Core.Services;

// Handles sending and receiving text messages, receipts, and typing indicators.
// All OnXxx callbacks fire on the UI thread (via the stored DispatcherQueue).
public sealed class MessagingService
{
    public static MessagingService Shared { get; } = new();

    public NetworkCoordinator? Coordinator { get; set; }
    private DispatcherQueue? _dq;

    // UI callbacks
    public Action<string, MessageEntry>?      OnMessageReceived { get; set; }  // peerIP, entry
    public Action<string, string, string>?    OnStatusUpdate    { get; set; }  // peerIP, messageId, status
    public Action<string, string, bool>?      OnTypingUpdate    { get; set; }  // peerIP, senderName, active

    private const int TcpPort = 54232;
    private readonly Dictionary<string, DateTime> _typingSentAt    = [];
    private readonly Dictionary<string, bool>     _lastTypingState = [];

    private MessagingService() { }

    public void SetDispatcherQueue(DispatcherQueue dq) => _dq = dq;

    // MARK: - Receive

    public void HandlePacket(ValidatedPacket packet)
    {
        switch (packet)
        {
            case ValidatedText   t: HandleText(t.Packet,    t.SenderIP); break;
            case ValidatedTyping t: HandleTyping(t.Packet,  t.SenderIP); break;
            case ValidatedReceipt r: HandleReceipt(r.Packet, r.SenderIP); break;
        }
    }

    // MARK: - Send text

    public void SendText(string text, string peerIP, string peerPublicKeyB64,
                         MessageEntry? replyTo = null)
    {
        var messageId = Guid.NewGuid().ToString("N").ToLowerInvariant();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var aad       = Encoding.UTF8.GetBytes(messageId);

        var replyPreview = replyTo is null ? null : ReplyPreviewText(replyTo);
        var entry = new MessageEntry
        {
            Sender          = ConfigStore.Shared.Config.Username,
            Text            = text,
            Incoming        = false,
            Timestamp       = timestamp,
            MessageId       = messageId,
            Status          = "Sending",
            ReadReceiptSent = false,
            ReplyToMessageId = replyTo?.MessageId,
            ReplyToPreview   = replyPreview,
            ReplyToSender    = replyTo?.Sender,
        };
        HistoryStore.Shared.Append(entry, peerIP);
        HistoryStore.Shared.Save();
        Dispatch(() => OnMessageReceived?.Invoke(peerIP, entry));

        (string nonceB64, string ctB64)? encrypted;
        try
        {
            encrypted = SessionCrypto.EncryptForPeer(
                KeyManager.Shared.PrivateKey, peerPublicKeyB64,
                Encoding.UTF8.GetBytes(text), aad);
        }
        catch
        {
            UpdateStatus("Failed", messageId, peerIP);
            return;
        }

        var packet = new Dictionary<string, object?>
        {
            ["type"]                  = "text",
            ["message_id"]            = messageId,
            ["timestamp"]             = timestamp,
            ["sender"]                = ConfigStore.Shared.Config.Username,
            ["sender_public_key_b64"] = KeyManager.Shared.PublicKeyB64,
            ["port"]                  = TcpPort,
            ["nonce"]                 = encrypted.Value.nonceB64,
            ["ciphertext"]            = encrypted.Value.ctB64,
        };
        if (replyTo?.MessageId is { } rid)
        {
            packet["reply_to_message_id"] = rid;
            if (replyPreview is not null) packet["reply_to_preview"] = replyPreview;
            if (replyTo.Sender is not null) packet["reply_to_sender"] = replyTo.Sender;
        }

        Task.Run(async () =>
        {
            var success = await FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort);
            Dispatch(() =>
            {
                var status = success ? "Sent" : "Queued";
                if (!success) QueuePending(messageId, text, peerPublicKeyB64, timestamp);
                UpdateStatus(status, messageId, peerIP);
                HistoryStore.Shared.Save();
            });
        });
    }

    public static string ReplyPreviewText(MessageEntry entry)
    {
        if (entry.Text.StartsWith("__FILE__:"))
        {
            var path = entry.Text["__FILE__:".Length..];
            return "📎 " + Path.GetFileName(path);
        }
        return entry.Text.Length <= 80 ? entry.Text : entry.Text[..80];
    }

    // MARK: - Send typing

    public void SendTyping(bool active, string peerIP, string peerPublicKeyB64)
    {
        var now = DateTime.UtcNow;
        if (!active && _lastTypingState.TryGetValue(peerIP, out var last) && !last) return;
        if (active && _lastTypingState.TryGetValue(peerIP, out var prev) && prev
            && _typingSentAt.TryGetValue(peerIP, out var sent) && (now - sent).TotalSeconds < 3) return;

        _lastTypingState[peerIP] = active;
        _typingSentAt[peerIP]    = now;

        var packet = new Dictionary<string, object?>
        {
            ["type"]                  = "typing",
            ["active"]                = active,
            ["sender"]                = ConfigStore.Shared.Config.Username,
            ["sender_public_key_b64"] = KeyManager.Shared.PublicKeyB64,
            ["port"]                  = TcpPort,
        };
        Task.Run(() => FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort));
    }

    // MARK: - Send receipt

    public void SendReceipt(string type, string messageId, string peerIP)
    {
        var packet = new Dictionary<string, object?>
        {
            ["type"]                  = type,
            ["message_id"]            = messageId,
            ["sender"]                = ConfigStore.Shared.Config.Username,
            ["sender_public_key_b64"] = KeyManager.Shared.PublicKeyB64,
            ["port"]                  = TcpPort,
        };
        Task.Run(() => FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort));
    }

    // MARK: - Deliver pending messages for a newly-online peer

    public void DeliverPending(string peerIP, string peerPublicKeyB64)
    {
        var pending = ConfigStore.Shared.Config.PendingMessages
            .Where(m => m.PeerPublicKeyB64 == peerPublicKeyB64).ToList();
        if (pending.Count == 0) return;

        foreach (var msg in pending)
        {
            var aad = Encoding.UTF8.GetBytes(msg.MessageId);
            (string nonceB64, string ctB64) encrypted;
            try { encrypted = SessionCrypto.EncryptForPeer(KeyManager.Shared.PrivateKey, peerPublicKeyB64, Encoding.UTF8.GetBytes(msg.Text), aad); }
            catch { continue; }

            var packet = new Dictionary<string, object?>
            {
                ["type"]                  = "text",
                ["message_id"]            = msg.MessageId,
                ["timestamp"]             = msg.Timestamp,
                ["sender"]                = ConfigStore.Shared.Config.Username,
                ["sender_public_key_b64"] = KeyManager.Shared.PublicKeyB64,
                ["port"]                  = TcpPort,
                ["nonce"]                 = encrypted.nonceB64,
                ["ciphertext"]            = encrypted.ctB64,
            };
            Task.Run(async () =>
            {
                var success = await FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort);
                if (success) Dispatch(() => UpdateStatus("Sent", msg.MessageId, peerIP));
            });
        }

        ConfigStore.Shared.Config.PendingMessages.RemoveAll(m => m.PeerPublicKeyB64 == peerPublicKeyB64);
        ConfigStore.Shared.Save();
    }

    // MARK: - Private receive handlers

    private void HandleText(TextPacket pkt, string ip)
    {
        var aad = Encoding.UTF8.GetBytes(pkt.MessageId);
        byte[] plaintext;
        try { plaintext = SessionCrypto.DecryptFromPeer(KeyManager.Shared.PrivateKey, pkt.SenderPublicKeyB64, pkt.Nonce, pkt.Ciphertext, aad); }
        catch { return; }

        // If the packet didn't carry a preview but we know the original, fill it in.
        var preview = pkt.ReplyToPreview;
        var replyToSender = pkt.ReplyToSender;
        if (!string.IsNullOrEmpty(pkt.ReplyToMessageId) && preview is null)
        {
            var orig = HistoryStore.Shared.Entries(ip)
                .FirstOrDefault(e => e.MessageId == pkt.ReplyToMessageId);
            if (orig is not null)
            {
                preview = ReplyPreviewText(orig);
                replyToSender ??= orig.Sender;
            }
        }

        var text  = Encoding.UTF8.GetString(plaintext);
        var entry = new MessageEntry
        {
            Sender = pkt.Sender, Text = text, Incoming = true,
            Timestamp = pkt.Timestamp, MessageId = pkt.MessageId,
            Status = "", ReadReceiptSent = false,
            ReplyToMessageId = pkt.ReplyToMessageId,
            ReplyToPreview   = preview,
            ReplyToSender    = replyToSender,
        };
        HistoryStore.Shared.Append(entry, ip);
        HistoryStore.Shared.Save();

        Dispatch(() =>
        {
            OnMessageReceived?.Invoke(ip, entry);
            OnTypingUpdate?.Invoke(ip, pkt.Sender, false);
        });
        SendReceipt("sent_receipt", pkt.MessageId, ip);
    }

    private void HandleTyping(TypingPacket pkt, string ip) =>
        Dispatch(() => OnTypingUpdate?.Invoke(ip, pkt.Sender, pkt.Active));

    private void HandleReceipt(ReceiptPacket pkt, string ip)
    {
        // sent_receipt = delivered to the peer (two grey ticks)
        // read_receipt = read by the peer (two blue ticks)
        var status = pkt.Type == "read_receipt" ? "Read" : "Delivered";

        // Don't downgrade Read → Delivered if a sent_receipt arrives after the read_receipt.
        var current = HistoryStore.Shared.Entries(ip)
            .FirstOrDefault(e => e.MessageId == pkt.MessageId)?.Status ?? "";
        if (status == "Delivered" && current == "Read") return;

        HistoryStore.Shared.UpdateStatus(status, pkt.MessageId, ip);
        HistoryStore.Shared.Save();
        Dispatch(() => OnStatusUpdate?.Invoke(ip, pkt.MessageId, status));
    }

    // MARK: - Helpers

    private void UpdateStatus(string status, string messageId, string peerIP)
    {
        HistoryStore.Shared.UpdateStatus(status, messageId, peerIP);
        OnStatusUpdate?.Invoke(peerIP, messageId, status);
    }

    private void QueuePending(string messageId, string text, string peerPublicKeyB64, double timestamp)
    {
        var username = ConfigStore.Shared.Config.Contacts
            .FirstOrDefault(c => c.PublicKeyB64 == peerPublicKeyB64)?.Username ?? "Unknown";
        ConfigStore.Shared.Config.PendingMessages.Add(new PendingMessageConfig
        {
            MessageId        = messageId,
            PeerPublicKeyB64 = peerPublicKeyB64,
            PeerUsername     = username,
            Text             = text,
            Timestamp        = timestamp,
        });
        ConfigStore.Shared.Save();
    }

    private static async Task<bool> FireTcpAsync(byte[] frame, string ip, int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await tcp.GetStream().WriteAsync(frame).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private void Dispatch(Action action) => _dq?.TryEnqueue(() => action());
}
