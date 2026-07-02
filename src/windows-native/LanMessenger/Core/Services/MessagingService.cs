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
    public Action<string, string>?            OnMessageDeleted  { get; set; }  // peerIP, messageId

    private const int TcpPort = 54232;
    private readonly Dictionary<string, DateTime> _typingSentAt    = [];
    private readonly Dictionary<string, bool>     _lastTypingState = [];

    // Pending-queue delivery bookkeeping (UI thread only). DeliverPending is
    // invoked on every discovery heartbeat (~1.5 s) so transiently failed
    // messages to an online peer retry promptly instead of sitting "Queued"
    // until the peer bounces. The in-flight set prevents double-sends while an
    // attempt is still on the wire; the per-message attempt clock backs off
    // retries against a persistently unreachable peer.
    private readonly HashSet<string>              _pendingInFlight   = [];
    private readonly Dictionary<string, DateTime> _pendingLastTry    = [];
    private static readonly TimeSpan PendingRetryInterval = TimeSpan.FromSeconds(10);

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
            case ValidatedDelete d: HandleDeleteMessage(d.Packet, d.SenderIP); break;
        }
    }

    // MARK: - Send text

    public void SendText(string text, string peerIP, string peerPublicKeyB64,
                         string? peerRelayIdHash = null, MessageEntry? replyTo = null)
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
            Status          = MessageStatus.Sending,
            ReadReceiptSent = false,
            ReplyToMessageId = replyTo?.MessageId,
            ReplyToPreview   = replyPreview,
            ReplyToSender    = replyTo?.Sender,
        };
        HistoryStore.Shared.Append(entry, peerIP);
        HistoryStore.Shared.Save();
        Dispatch(() => OnMessageReceived?.Invoke(peerIP, entry));
        LanLogger.Info("Send", $"text msgId={messageId} peer={peerIP} bytes={text.Length}");

        (string nonceB64, string ctB64)? encrypted;
        try
        {
            encrypted = SessionCrypto.EncryptForPeer(
                KeyManager.Shared.PrivateKey, peerPublicKeyB64,
                Encoding.UTF8.GetBytes(text), aad);
        }
        catch (Exception ex)
        {
            LanLogger.Error("Send", $"encrypt failed msgId={messageId} peer={peerIP}", ex);
            ApplyStatus(MessageStatus.Failed, messageId, peerIP);
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

        var capturedNonce = encrypted.Value.nonceB64;
        var capturedCt    = encrypted.Value.ctB64;
        Task.Run(async () =>
        {
            var success = await FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort, $"text msgId={messageId}");
            if (success)
            {
                LanLogger.Info("Send", $"TCP delivered msgId={messageId} peer={peerIP}");
            }
            else
            {
                LanLogger.Info("Send", $"TCP failed msgId={messageId} peer={peerIP} — queueing locally and falling back to relay");
            }
            Dispatch(() =>
            {
                var status = success ? MessageStatus.Sent : MessageStatus.Queued;
                if (!success) QueuePending(messageId, peerIP, text, peerPublicKeyB64, peerRelayIdHash,
                                           capturedCt, capturedNonce, timestamp);
                // ApplyStatus persists when the rank check passes; nothing else
                // to save here. Crucially, if a "Delivered" receipt already
                // arrived between the WriteAsync and this dispatch, the rank
                // check drops this update and the message correctly stays at
                // two ticks instead of regressing to one.
                ApplyStatus(status, messageId, peerIP);
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
        Task.Run(() => FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort, $"typing active={active}"));
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
        Task.Run(async () =>
        {
            var ok = await FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort, $"{type} msgId={messageId}");
            if (!ok) LanLogger.Warn("Receipt", $"failed to send {type} msgId={messageId} peer={peerIP}");
        });
    }

    // MARK: - Send delete notice

    // "Delete for everyone" — best-effort, unencrypted notice that the sender's
    // own outgoing message should be marked deleted on the receiver's side too.
    public void SendDeleteMessage(string messageId, string peerIP)
    {
        var packet = new Dictionary<string, object?>
        {
            ["type"]                  = "delete_message",
            ["message_id"]            = messageId,
            ["sender"]                = ConfigStore.Shared.Config.Username,
            ["sender_public_key_b64"] = KeyManager.Shared.PublicKeyB64,
            ["port"]                  = TcpPort,
        };
        Task.Run(async () =>
        {
            var ok = await FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort, $"delete_message msgId={messageId}");
            if (!ok) LanLogger.Warn("Delete", $"failed to send delete_message msgId={messageId} peer={peerIP}");
        });
    }

    // MARK: - Deliver pending messages for a newly-online peer

    public void DeliverPending(string peerIP, string peerPublicKeyB64)
    {
        var now = DateTime.UtcNow;
        var pending = ConfigStore.Shared.Config.PendingMessages
            .Where(m => m.PeerPublicKeyB64 == peerPublicKeyB64
                        && !_pendingInFlight.Contains(m.MessageId)
                        && (!_pendingLastTry.TryGetValue(m.MessageId, out var last)
                            || now - last >= PendingRetryInterval))
            .ToList();
        if (pending.Count == 0) return;
        LanLogger.Info("Send", $"delivering {pending.Count} pending msgs to peer={peerIP}");

        foreach (var msg in pending)
        {
            _pendingInFlight.Add(msg.MessageId);
            _pendingLastTry[msg.MessageId] = now;
            var aad = Encoding.UTF8.GetBytes(msg.MessageId);
            (string nonceB64, string ctB64) encrypted;
            try { encrypted = SessionCrypto.EncryptForPeer(KeyManager.Shared.PrivateKey, peerPublicKeyB64, Encoding.UTF8.GetBytes(msg.Text), aad); }
            catch (Exception ex)
            {
                LanLogger.Error("Send", $"pending encrypt failed msgId={msg.MessageId}", ex);
                _pendingInFlight.Remove(msg.MessageId);
                continue;
            }

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
            var msgId = msg.MessageId;
            Task.Run(async () =>
            {
                var success = await FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort, $"pending msgId={msgId}");
                // Remove only after confirmed delivery so a TCP failure doesn't
                // silently drop the message from the queue. On failure, clearing
                // the in-flight flag lets the next heartbeat retry after the
                // per-message backoff elapses.
                Dispatch(() =>
                {
                    _pendingInFlight.Remove(msgId);
                    if (!success) return;
                    _pendingLastTry.Remove(msgId);
                    ApplyStatus(MessageStatus.Sent, msgId, peerIP);
                    ConfigStore.Shared.Config.PendingMessages.RemoveAll(m => m.MessageId == msgId);
                    ConfigStore.Shared.Save();
                });
            });
        }
    }

    // MARK: - Private receive handlers

    private void HandleText(TextPacket pkt, string ip)
    {
        // Duplicate suppression: heartbeat-driven queue retries (and a sender
        // whose sent_receipt got lost) can legitimately re-send a message we
        // already have. Don't append it twice — but do re-acknowledge, because
        // a re-send means the sender never saw our first receipt.
        if (HistoryStore.Shared.Entries(ip).Any(e => e.MessageId == pkt.MessageId))
        {
            LanLogger.Info("Recv", $"duplicate text msgId={pkt.MessageId} peer={ip} — re-sending receipt only");
            SendReceipt("sent_receipt", pkt.MessageId, ip);
            return;
        }

        var aad = Encoding.UTF8.GetBytes(pkt.MessageId);
        byte[] plaintext;
        try { plaintext = SessionCrypto.DecryptFromPeer(KeyManager.Shared.PrivateKey, pkt.SenderPublicKeyB64, pkt.Nonce, pkt.Ciphertext, aad); }
        catch (Exception ex)
        {
            // Silent receiver-side decrypt failure was historically a primary reason
            // for "single check mark" — sender never got a sent_receipt. Log so the
            // user can correlate failed deliveries with key mismatches.
            LanLogger.Error("Recv", $"decrypt failed msgId={pkt.MessageId} peer={ip}", ex);
            return;
        }
        LanLogger.Info("Recv", $"text msgId={pkt.MessageId} peer={ip} bytes={plaintext.Length}");

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

    private void HandleDeleteMessage(ReceiptPacket pkt, string ip)
    {
        LanLogger.Info("Recv", $"delete_message msgId={pkt.MessageId} peer={ip}");
        HistoryStore.Shared.MarkDeleted(pkt.MessageId, ip);
        HistoryStore.Shared.Save();
        Dispatch(() => OnMessageDeleted?.Invoke(ip, pkt.MessageId));
    }

    private void HandleReceipt(ReceiptPacket pkt, string ip)
    {
        // sent_receipt = delivered to the peer (two grey ticks)
        // read_receipt = read by the peer (two blue ticks)
        var status = pkt.Type == "read_receipt" ? MessageStatus.Read : MessageStatus.Delivered;
        LanLogger.Info("Recv", $"{pkt.Type} msgId={pkt.MessageId} peer={ip}");
        // ApplyStatus is rank-aware: a late "Sent" dispatch from the sender's
        // own TCP-write completion cannot regress this. See MessageStatus.cs.
        ApplyStatus(status, pkt.MessageId, ip);
    }

    // MARK: - Helpers

    // Single funnel for every status mutation. Always rank-aware so the
    // races described in MessageStatus.cs can't downgrade a message.
    private void ApplyStatus(string status, string messageId, string peerIP)
    {
        var applied = HistoryStore.Shared.UpdateStatus(status, messageId, peerIP);
        if (!applied) return;
        HistoryStore.Shared.Save();
        OnStatusUpdate?.Invoke(peerIP, messageId, status);
        LanLogger.Info("Status", $"msgId={messageId} peer={peerIP} -> {status}");
    }

    private void QueuePending(
        string messageId,
        string peerIP,
        string text,
        string peerPublicKeyB64,
        string? peerRelayIdHash,
        string ciphertextB64,
        string nonceB64,
        double timestamp)
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

        // Upload to cloud relay (only if peer was confirmed offline before sending —
        // the relay hash is null when the peer was online, preventing spurious relay use).
        if (string.IsNullOrEmpty(peerRelayIdHash))
        {
            LanLogger.Info("Relay", $"skip store msgId={messageId} — peer online or has no relay_id_hash; message queued locally only");
            return;
        }

        LanLogger.Info("Relay", $"store msgId={messageId} peer={peerPublicKeyB64[..Math.Min(8, peerPublicKeyB64.Length)]} — uploading to cloud relay mailbox");
        HistoryStore.Shared.MarkRelayDelivery(messageId, peerIP);
        _ = RelayClient.Shared.StoreAsync(peerRelayIdHash, messageId, ciphertextB64, nonceB64, timestamp);
    }

    // MARK: - Handle relay-delivered messages (from cloud Worker)

    /// Decrypts and processes a message that arrived via the cloud relay.
    /// Call from AppModel after FetchPendingAsync().
    public void HandleRelayMessage(RelayPendingMessage msg, string fromStoredIP)
    {
        var aad = Encoding.UTF8.GetBytes(msg.MessageId);
        byte[] plaintext;
        try
        {
            plaintext = SessionCrypto.DecryptFromPeer(
                KeyManager.Shared.PrivateKey, msg.SenderPublicKeyB64,
                msg.NonceB64, msg.CiphertextB64, aad);
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Relay", $"failed to decrypt relay message {msg.MessageId}: {ex.Message}");
            return;
        }

        var text = Encoding.UTF8.GetString(plaintext);

        // Deduplicate: skip if we already have this message in history.
        if (HistoryStore.Shared.Entries(fromStoredIP).Any(e => e.MessageId == msg.MessageId))
            return;

        var entry = new MessageEntry
        {
            Sender          = msg.SenderUsername,
            Text            = text,
            Incoming        = true,
            Timestamp       = msg.Timestamp,
            MessageId       = msg.MessageId,
            Status          = "",
            ReadReceiptSent = false,
            DeliveryPath    = "relay",
        };
        HistoryStore.Shared.Append(entry, fromStoredIP);
        HistoryStore.Shared.Save();
        Dispatch(() => OnMessageReceived?.Invoke(fromStoredIP, entry));
        LanLogger.Info("Relay", $"delivered relay msg {msg.MessageId} from {msg.SenderUsername} via ip={fromStoredIP}");

        // Send sent_receipt so the sender sees "Delivered" for their relayed message.
        // Only attempt when the IP is a real address (not a synthetic "relay-…" placeholder).
        if (!fromStoredIP.StartsWith("relay-", StringComparison.Ordinal))
            SendReceipt("sent_receipt", msg.MessageId, fromStoredIP);

        // Delete from relay now that we've processed it (best-effort)
        _ = RelayClient.Shared.DeleteAsync(msg.MessageId);
    }

    private static async Task<bool> FireTcpAsync(byte[] frame, string ip, int port, string description)
    {
        // Two attempts with a short pause. A single SYN lost to Wi-Fi power
        // save or a peer's listener mid-rebuild is common on real LANs; without
        // the retry, one lost packet turns into a "Queued" message even though
        // the peer is online. Retrying is safe: a failed attempt either never
        // connected or delivered a partial frame, which the receiver discards.
        if (await FireTcpOnceAsync(frame, ip, port, description).ConfigureAwait(false)) return true;
        await Task.Delay(300).ConfigureAwait(false);
        return await FireTcpOnceAsync(frame, ip, port, $"{description} (retry)").ConfigureAwait(false);
    }

    private static async Task<bool> FireTcpOnceAsync(byte[] frame, string ip, int port, string description)
    {
        // One-shot TCP per packet. We explicitly Shutdown(Send) and wait for
        // the peer's FIN with a short read before closing — without this, the
        // OS sometimes aborted the connection (RST) between WriteAsync and
        // Dispose on slow / loaded Windows machines, causing the receiver to
        // drop the in-flight frame. That was a major source of cross-platform
        // delivery failures with macOS peers.
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient { NoDelay = true };
            await tcp.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            // Linger off, but with a short timeout — kernel will gracefully
            // flush the send buffer instead of resetting on Dispose.
            tcp.LingerState = new System.Net.Sockets.LingerOption(true, 2);

            var stream = tcp.GetStream();
            await stream.WriteAsync(frame).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            // Half-close: tells the peer we're done sending. The peer's read
            // loop will see EOF after consuming the frame and close its end.
            try { tcp.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send); } catch { }

            // Brief drain so the kernel actually transmits before we Dispose.
            // We don't care what (if anything) the peer sends — we just need
            // to give the FIN/data exchange ~1 s to complete.
            var drainBuf = new byte[1];
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await stream.ReadAsync(drainBuf.AsMemory(0, 1), drainCts.Token).ConfigureAwait(false); }
            catch { /* timeout or peer reset — frame is already on the wire */ }

            return true;
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Net", $"FireTcp failed peer={ip}:{port} desc={description}: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    private void Dispatch(Action action) => _dq?.TryEnqueue(() => action());
}
