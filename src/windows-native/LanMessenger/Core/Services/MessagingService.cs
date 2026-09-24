using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking;
using LanMessenger.Core.Networking.Media;
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

    // UI callbacks. Every one names the conversation by its id (PeerId) — the
    // peer's identity key — never by the address a packet happened to arrive
    // from. DHCP hands the same addresses to different machines, so an address
    // is not a person.
    public Action<string, MessageEntry>?      OnMessageReceived { get; set; }  // peer, entry
    public Action<string, string, string>?    OnStatusUpdate    { get; set; }  // peer, messageId, status
    public Action<string, string, bool>?      OnTypingUpdate    { get; set; }  // peer, senderName, active
    public Action<string, string>?            OnMessageDeleted  { get; set; }  // peer, messageId
    // peer, messageId, newText, editedAt
    public Action<string, string, string, double>? OnMessageEdited { get; set; }

    /// <summary>
    /// Whether <c>ip</c> is an address the device holding <c>key</c> has been
    /// seen at. The gate for packets that only <i>claim</i> a sender — typing,
    /// receipts and delete_message are not encrypted, so their
    /// sender_public_key_b64 is whatever the sender typed. Encrypted packets
    /// prove the key by decrypting and do not need this. Filed by source
    /// address, those packets at least needed a completed TCP connection from
    /// that address; filed by a bare claimed key they would need nothing at
    /// all, since public keys are public. Binding the claim to an address that
    /// key is known at keeps them exactly as hard to forge as before.
    /// Unset means "accept", so the service stays usable without an AppModel.
    /// </summary>
    public Func<string, string, bool>?        IsBoundAddress    { get; set; }  // key, ip

    /// <summary>
    /// A message decrypted under <c>key</c> arrived from <c>ip</c> — the device
    /// holding that key is there now. How a peer whose discovery beacons never
    /// reach us still gets its replies: the address is learned from traffic that
    /// proved who sent it, never from a claim. Fresh messages only; a duplicate
    /// may be a replay.
    /// </summary>
    public Action<string, string, string>?    OnPeerAddressProven { get; set; } // key, ip, senderName
    // Fired once the cloud relay Worker confirms an outgoing message was
    // actually stored — not when the upload is merely attempted. Lets the UI
    // show the "via relay" badge promptly instead of only after the
    // recipient later retrieves the message (which is what a full history
    // reload previously depended on).
    public Action<string>?                    OnDeliveryPathUpdate { get; set; } // messageId
    // Fired whenever a direct outbound TCP send to a peer's live address
    // actually lands — a completed handshake + write is at least as strong
    // proof of reachability as an inbound discovery beacon, so a peer we are
    // talking to stays online between its own beacons. Names the key the send
    // was addressed to, never "whoever is at this IP".
    public Action<string, string>?            OnPeerReachable      { get; set; } // peer key, ip

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

    // Cloud-relay outbox retry bookkeeping — kept separate from the local TCP
    // retry state above so the two transports retry independently and never
    // block each other.
    private readonly HashSet<string>              _relayOutboxInFlight = [];
    private readonly Dictionary<string, DateTime> _relayOutboxLastTry  = [];

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
            case ValidatedEdit   e: HandleEditMessage(e.Packet,   e.SenderIP); break;
        }
    }

    // MARK: - Send text

    /// <summary>
    /// Sends to <paramref name="peerPublicKeyB64"/>, filed under that key — the
    /// same key it is encrypted to. <paramref name="peerIP"/> is where that
    /// device is right now, or "" when it is not on the LAN: the message is
    /// then queued and relayed at once rather than dialled at a remembered
    /// address that may belong to somebody else by now.
    /// </summary>
    public void SendText(string text, string peerIP, string peerPublicKeyB64,
                         string? peerRelayIdHash = null, MessageEntry? replyTo = null)
    {
        var peer = peerPublicKeyB64;
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
        HistoryStore.Shared.Append(entry, peer);
        HistoryStore.Shared.Save();
        Dispatch(() => OnMessageReceived?.Invoke(peer, entry));
        LanLogger.Info("Send", $"text msgId={messageId} peer={peer[..Math.Min(8, peer.Length)]} addr={peerIP} bytes={text.Length}");

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
            ApplyStatus(MessageStatus.Failed, messageId, peer);
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

        if (string.IsNullOrEmpty(peerIP))
        {
            LanLogger.Info("Send", $"peer {peer[..Math.Min(8, peer.Length)]} not on the LAN — queueing msgId={messageId} for relay/redelivery");
            QueuePending(messageId, text, peerPublicKeyB64, peerRelayIdHash, timestamp);
            ApplyStatus(MessageStatus.Queued, messageId, peer);
            return;
        }

        Task.Run(async () =>
        {
            var success = await FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort, $"text msgId={messageId}", peer);
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
                if (!success) QueuePending(messageId, text, peerPublicKeyB64, peerRelayIdHash, timestamp);
                // ApplyStatus persists when the rank check passes; nothing else
                // to save here. Crucially, if a "Delivered" receipt already
                // arrived between the WriteAsync and this dispatch, the rank
                // check drops this update and the message correctly stays at
                // two ticks instead of regressing to one.
                ApplyStatus(status, messageId, peer);
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
        // Unreachable today, since an audit row offers neither reply nor edit,
        // but this is one of the places that inspects text for a marker.
        if (RemoteAuditRecord.IsAudit(entry.Text)) return RemoteAuditRecord.SummaryOf(entry.Text);
        return entry.Text.Length <= 80 ? entry.Text : entry.Text[..80];
    }

    // MARK: - Send typing

    public void SendTyping(bool active, string peerIP, string peerPublicKeyB64)
    {
        if (string.IsNullOrEmpty(peerIP)) return;
        var peer = peerPublicKeyB64;
        var now = DateTime.UtcNow;
        if (!active && _lastTypingState.TryGetValue(peer, out var last) && !last) return;
        if (active && _lastTypingState.TryGetValue(peer, out var prev) && prev
            && _typingSentAt.TryGetValue(peer, out var sent) && (now - sent).TotalSeconds < 3) return;

        _lastTypingState[peer] = active;
        _typingSentAt[peer]    = now;

        var packet = new Dictionary<string, object?>
        {
            ["type"]                  = "typing",
            ["active"]                = active,
            ["sender"]                = ConfigStore.Shared.Config.Username,
            ["sender_public_key_b64"] = KeyManager.Shared.PublicKeyB64,
            ["port"]                  = TcpPort,
        };
        Task.Run(() => FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort, $"typing active={active}", peer));
    }

    // MARK: - Send receipt

    public void SendReceipt(string type, string messageId, string peerIP)
    {
        if (string.IsNullOrEmpty(peerIP)) return;
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
    public void SendDeleteMessage(string messageId, string peerIP,
                                  string? peerPublicKeyB64 = null,
                                  string? peerRelayIdHash = null)
    {
        // Drop the queued copy too — delivering a message the sender has since
        // deleted would be worse than not delivering it at all.
        DropPendingMessage(messageId);

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
            if (ok)
            {
                LanLogger.Info("Delete", $"delivered delete msgId={messageId} peer={peerIP}");
                return;
            }
            LanLogger.Info("Delete", $"delete not delivered over LAN msgId={messageId} peer={peerIP} — trying relay");
            if (string.IsNullOrEmpty(peerPublicKeyB64)) return;
            await SendRelayControlAsync(
                new RelayControlEnvelope(RelayControlOp.Delete, messageId, null,
                                         DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0),
                peerPublicKeyB64, peerRelayIdHash);
        });
    }

    /// Removes a still-undelivered message from the local pending queue.
    private static void DropPendingMessage(string messageId)
    {
        var pending = ConfigStore.Shared.Config.PendingMessages;
        var removed = pending.RemoveAll(m => m.MessageId == messageId);
        if (removed == 0) return;
        ConfigStore.Shared.Save();
        LanLogger.Info("Delete", $"dropped queued msgId={messageId} before delivery");
    }

    // MARK: - Send edit_message

    /// <summary>
    /// Sends replacement text for a message we already sent.
    ///
    /// Best-effort in the same sense as delete_message — one TCP write, no
    /// queue and no retry — with one important exception: if the original is
    /// still sitting in the pending queue (it never reached the peer), the
    /// queued copy is rewritten so the edit is what eventually gets delivered,
    /// as the message's first and only version.
    /// </summary>
    public void SendEditMessage(string messageId, string newText, string peerIP,
                                string peerPublicKeyB64, double editedAt,
                                string? peerRelayIdHash = null)
    {
        RewritePendingMessage(messageId, newText);

        // AAD is the ORIGINAL message_id, exactly as for the `text` packet that
        // carried the first version.
        var aad = Encoding.UTF8.GetBytes(messageId);
        (string nonceB64, string ctB64) encrypted;
        try
        {
            encrypted = SessionCrypto.EncryptForPeer(
                KeyManager.Shared.PrivateKey, peerPublicKeyB64,
                Encoding.UTF8.GetBytes(newText), aad);
        }
        catch (Exception ex)
        {
            LanLogger.Error("Edit", $"encrypt failed msgId={messageId} peer={peerIP}", ex);
            return;
        }

        var packet = new Dictionary<string, object?>
        {
            ["type"]                  = "edit_message",
            ["message_id"]            = messageId,
            ["timestamp"]             = editedAt,
            ["sender"]                = ConfigStore.Shared.Config.Username,
            ["sender_public_key_b64"] = KeyManager.Shared.PublicKeyB64,
            ["port"]                  = TcpPort,
            ["nonce"]                 = encrypted.nonceB64,
            ["ciphertext"]            = encrypted.ctB64,
        };
        Task.Run(async () =>
        {
            var ok = await FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort, $"edit_message msgId={messageId}");
            if (ok)
            {
                LanLogger.Info("Edit", $"delivered edit msgId={messageId} peer={peerIP}");
                return;
            }
            LanLogger.Info("Edit", $"edit not delivered over LAN msgId={messageId} peer={peerIP} — trying relay");
            await SendRelayControlAsync(
                new RelayControlEnvelope(RelayControlOp.Edit, messageId, newText, editedAt),
                peerPublicKeyB64, peerRelayIdHash);
        });
    }

    // MARK: - Relay control records (offline edit / delete)

    /// <summary>
    /// Carries an edit or delete to a peer who isn't reachable on the LAN, by
    /// dropping an encrypted control record in their relay mailbox. They apply
    /// it on their next poll. No-op when the relay isn't configured for this
    /// peer — the change then stays local, as it did before.
    ///
    /// The record gets its own fresh id rather than the target's: the Worker
    /// dedups /store by message_id and answers a repeat with
    /// {ok:true,duplicate:true}, so re-posting under the original's id would be
    /// dropped while reporting success.
    /// </summary>
    private static async Task SendRelayControlAsync(RelayControlEnvelope envelope,
                                                    string peerPublicKeyB64,
                                                    string? peerRelayIdHash)
    {
        if (string.IsNullOrEmpty(peerRelayIdHash))
        {
            LanLogger.Info("Relay", $"skip {envelope.Op} control for target={envelope.Target} — peer has no relay_id_hash");
            return;
        }

        var recordId = RelayControlEnvelope.NewRecordId();
        (string nonceB64, string ctB64) encrypted;
        try
        {
            encrypted = SessionCrypto.EncryptForPeer(
                KeyManager.Shared.PrivateKey, peerPublicKeyB64,
                Encoding.UTF8.GetBytes(envelope.Encoded()),
                Encoding.UTF8.GetBytes(recordId));
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Relay", $"encrypt failed for {envelope.Op} control target={envelope.Target}: {ex.Message}");
            return;
        }

        var ok = await RelayClient.Shared.StoreAsync(
            peerRelayIdHash, recordId, encrypted.ctB64, encrypted.nonceB64, envelope.At);
        LanLogger.Info("Relay", ok
            ? $"stored {envelope.Op} control record={recordId} target={envelope.Target}"
            : $"failed to store {envelope.Op} control target={envelope.Target}");
    }

    /// <summary>
    /// Rewrites a still-undelivered queued message so the peer receives the
    /// edited text rather than the superseded original.
    /// </summary>
    private static void RewritePendingMessage(string messageId, string newText)
    {
        var pending = ConfigStore.Shared.Config.PendingMessages;
        var target = pending.FirstOrDefault(m => m.MessageId == messageId);
        if (target is null) return;
        target.Text = newText;
        // Deliberately NOT clearing RelayStored to force a re-upload: the Worker
        // dedups /store on message_id and answers a repeat with
        // {ok:true,duplicate:true}, so the re-upload would be discarded while
        // reporting success and the peer would still get the original text. A
        // relay copy is superseded by an edit control record instead (see
        // SendRelayControlAsync).
        ConfigStore.Shared.Save();
        LanLogger.Info("Edit", $"rewrote queued msgId={messageId} before delivery");
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
                var success = await FireTcpAsync(FrameCodec.EncodeDict(packet), peerIP, TcpPort, $"pending msgId={msgId}", peerPublicKeyB64);
                // Remove only after confirmed delivery so a TCP failure doesn't
                // silently drop the message from the queue. On failure, clearing
                // the in-flight flag lets the next heartbeat retry after the
                // per-message backoff elapses.
                Dispatch(() =>
                {
                    _pendingInFlight.Remove(msgId);
                    if (!success) return;
                    _pendingLastTry.Remove(msgId);
                    ApplyStatus(MessageStatus.Sent, msgId, peerPublicKeyB64);
                    // Note whether this message had already landed on the relay
                    // before removing it from the queue — if so, clean up the
                    // Worker copy now that direct LAN delivery beat it there.
                    // Best-effort: even if this fails, the global dedup in
                    // HandleRelayMessage prevents a stale mailbox copy from
                    // ever showing up as a duplicate.
                    var wasRelayStored = ConfigStore.Shared.Config.PendingMessages
                        .FirstOrDefault(m => m.MessageId == msgId)?.RelayStored ?? false;
                    ConfigStore.Shared.Config.PendingMessages.RemoveAll(m => m.MessageId == msgId);
                    ConfigStore.Shared.Save();
                    if (wasRelayStored) _ = RelayClient.Shared.DeleteAsync(msgId);
                });
            });
        }
    }

    // MARK: - Private receive handlers

    private void HandleText(TextPacket pkt, string ip)
    {
        // Decrypt FIRST. Opening under the claimed key is what proves who sent
        // it — after this, pkt.SenderPublicKeyB64 is an identity rather than a
        // claim, and it is the conversation the message belongs to. Checking
        // for a duplicate before this answered a forged packet naming a real
        // message id with a receipt, confirming to a stranger that we had it.
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
        var peer = pkt.SenderPublicKeyB64;

        // Duplicate suppression: heartbeat-driven queue retries (and a sender
        // whose sent_receipt got lost) can legitimately re-send a message we
        // already have. Don't append it twice — but do re-acknowledge, because
        // a re-send means the sender never saw our first receipt.
        if (HistoryStore.Shared.Entries(peer).Any(e => e.MessageId == pkt.MessageId))
        {
            LanLogger.Info("Recv", $"duplicate text msgId={pkt.MessageId} peer={ip} — re-sending receipt only");
            SendReceipt("sent_receipt", pkt.MessageId, ip);
            return;
        }
        LanLogger.Info("Recv", $"text msgId={pkt.MessageId} peer={ip} key={peer[..Math.Min(8, peer.Length)]} bytes={plaintext.Length}");
        Dispatch(() => OnPeerAddressProven?.Invoke(peer, ip, pkt.Sender));

        // If the packet didn't carry a preview but we know the original, fill it in.
        var preview = pkt.ReplyToPreview;
        var replyToSender = pkt.ReplyToSender;
        if (!string.IsNullOrEmpty(pkt.ReplyToMessageId) && preview is null)
        {
            var orig = HistoryStore.Shared.Entries(peer)
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
        HistoryStore.Shared.Append(entry, peer);
        HistoryStore.Shared.Save();

        Dispatch(() =>
        {
            OnMessageReceived?.Invoke(peer, entry);
            OnTypingUpdate?.Invoke(peer, pkt.Sender, false);
        });
        SendReceipt("sent_receipt", pkt.MessageId, ip);
    }

    /// <summary>The conversation an unencrypted packet belongs to, or null to drop it.</summary>
    private string? ClaimedPeer(string key, string ip, string what)
    {
        if (!PeerId.IsKey(key))
        {
            LanLogger.Info("Recv", $"{what} from {ip} dropped — no usable sender key");
            return null;
        }
        if (IsBoundAddress is { } bound && !bound(key, ip))
        {
            LanLogger.Info("Recv", $"{what} from {ip} dropped — not an address {key[..8]} is known at");
            return null;
        }
        return key;
    }

    private void HandleTyping(TypingPacket pkt, string ip)
    {
        if (ClaimedPeer(pkt.SenderPublicKeyB64, ip, "typing") is not { } peer) return;
        Dispatch(() => OnTypingUpdate?.Invoke(peer, pkt.Sender, pkt.Active));
    }

    private void HandleDeleteMessage(ReceiptPacket pkt, string ip)
    {
        LanLogger.Info("Recv", $"delete_message msgId={pkt.MessageId} peer={ip}");
        if (ClaimedPeer(pkt.SenderPublicKeyB64, ip, "delete_message") is not { } peer) return;
        if (!HistoryStore.Shared.MarkDeleted(pkt.MessageId, peer, requireIncoming: true))
        {
            LanLogger.Info("Delete", $"ignored inbound delete msgId={pkt.MessageId} peer={ip} — no deletable incoming message");
            return;
        }
        HistoryStore.Shared.Save();
        Dispatch(() => OnMessageDeleted?.Invoke(peer, pkt.MessageId));
    }

    // Applies an inbound edit: replaces the stored text of the peer's own
    // earlier message. HistoryStore.ApplyEdit enforces that only an *incoming*
    // entry can be rewritten — the peer knows the message_id of everything we
    // sent them, so an edit naming one of our outgoing messages is refused.
    private void HandleEditMessage(TextPacket pkt, string ip)
    {
        var aad = Encoding.UTF8.GetBytes(pkt.MessageId);
        byte[] plaintext;
        try
        {
            plaintext = SessionCrypto.DecryptFromPeer(
                KeyManager.Shared.PrivateKey, pkt.SenderPublicKeyB64,
                pkt.Nonce, pkt.Ciphertext, aad);
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Edit", $"decrypt failed for inbound edit msgId={pkt.MessageId} peer={ip}: {ex.Message}");
            return;
        }

        var newText = Encoding.UTF8.GetString(plaintext);
        // Decrypted, so the key is proven: only this device's own conversation
        // is searched, and within it only its incoming messages.
        var peer = pkt.SenderPublicKeyB64;
        if (!HistoryStore.Shared.ApplyEdit(pkt.MessageId, peer, newText, pkt.Timestamp, requireIncoming: true))
        {
            LanLogger.Info("Edit", $"ignored inbound edit msgId={pkt.MessageId} peer={ip} — no editable incoming message");
            return;
        }
        HistoryStore.Shared.Save();
        LanLogger.Info("Recv", $"edit_message applied msgId={pkt.MessageId} peer={ip}");
        Dispatch(() => OnMessageEdited?.Invoke(peer, pkt.MessageId, newText, pkt.Timestamp));
    }

    /// <summary>
    /// Applies a relay control record. Routed through the same HistoryStore
    /// entry points as the LAN edit_message / delete_message packets, so the
    /// requireIncoming gate applies identically: a peer can only edit or delete
    /// their own messages, never ours.
    /// </summary>
    private void ApplyRelayControl(RelayControlEnvelope envelope, string peer)
    {
        var who = peer[..Math.Min(8, peer.Length)];
        if (envelope.Op == RelayControlOp.Edit)
        {
            var newText = envelope.Text ?? "";
            if (!HistoryStore.Shared.ApplyEdit(envelope.Target, peer, newText, envelope.At, requireIncoming: true))
            {
                LanLogger.Info("Relay", $"ignored relayed edit target={envelope.Target} peer={who} — no editable incoming message");
                return;
            }
            HistoryStore.Shared.Save();
            LanLogger.Info("Relay", $"applied relayed edit target={envelope.Target} peer={who}");
            Dispatch(() => OnMessageEdited?.Invoke(peer, envelope.Target, newText, envelope.At));
        }
        else
        {
            if (!HistoryStore.Shared.MarkDeleted(envelope.Target, peer, requireIncoming: true))
            {
                LanLogger.Info("Relay", $"ignored relayed delete target={envelope.Target} peer={who} — no deletable incoming message");
                return;
            }
            HistoryStore.Shared.Save();
            LanLogger.Info("Relay", $"applied relayed delete target={envelope.Target} peer={who}");
            Dispatch(() => OnMessageDeleted?.Invoke(peer, envelope.Target));
        }
    }

    private void HandleReceipt(ReceiptPacket pkt, string ip)
    {
        // sent_receipt = delivered to the peer (two grey ticks)
        // read_receipt = read by the peer (two blue ticks)
        var status = pkt.Type == "read_receipt" ? MessageStatus.Read : MessageStatus.Delivered;
        LanLogger.Info("Recv", $"{pkt.Type} msgId={pkt.MessageId} peer={ip}");
        if (ClaimedPeer(pkt.SenderPublicKeyB64, ip, pkt.Type) is not { } peer) return;
        // ApplyStatus is rank-aware: a late "Sent" dispatch from the sender's
        // own TCP-write completion cannot regress this. See MessageStatus.cs.
        ApplyStatus(status, pkt.MessageId, peer);
    }

    // MARK: - Helpers

    // Single funnel for every status mutation. Always rank-aware so the
    // races described in MessageStatus.cs can't downgrade a message.
    private void ApplyStatus(string status, string messageId, string peer)
    {
        var applied = HistoryStore.Shared.UpdateStatus(status, messageId, peer);
        if (!applied) return;
        HistoryStore.Shared.Save();
        OnStatusUpdate?.Invoke(peer, messageId, status);
        LanLogger.Info("Status", $"msgId={messageId} peer={peer[..Math.Min(8, peer.Length)]} -> {status}");
    }

    private void QueuePending(
        string messageId,
        string text,
        string peerPublicKeyB64,
        string? peerRelayIdHash,
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
        RetryRelayStore(messageId, peerRelayIdHash);
    }

    // MARK: - Cloud relay outbox retry

    /// Attempts (or retries) uploading a queued message to the cloud relay
    /// Worker. Re-encrypts fresh on every call — a new nonce per attempt,
    /// same pattern as DeliverPending's direct-TCP retries — since the
    /// pending queue only persists plaintext. Backed off per-message so a
    /// persistently unreachable Worker doesn't get hammered; the in-flight
    /// guard prevents a concurrent duplicate attempt for the same message.
    /// The relay poll timer calls this for every not-yet-confirmed pending
    /// message (see AppModel.RetryRelayOutbox) so a store that failed on
    /// the first attempt — transient network blip, Worker cold start, a
    /// full inbox — eventually gets through instead of being lost forever.
    public void RetryRelayStore(string messageId, string peerRelayIdHash)
    {
        if (_relayOutboxInFlight.Contains(messageId)) return;
        if (_relayOutboxLastTry.TryGetValue(messageId, out var last)
            && DateTime.UtcNow - last < PendingRetryInterval) return;

        var msg = ConfigStore.Shared.Config.PendingMessages.FirstOrDefault(m => m.MessageId == messageId);
        if (msg is null || msg.RelayStored) return;

        _relayOutboxInFlight.Add(messageId);
        _relayOutboxLastTry[messageId] = DateTime.UtcNow;

        var aad = Encoding.UTF8.GetBytes(messageId);
        (string nonceB64, string ctB64) encrypted;
        try
        {
            encrypted = SessionCrypto.EncryptForPeer(
                KeyManager.Shared.PrivateKey, msg.PeerPublicKeyB64,
                Encoding.UTF8.GetBytes(msg.Text), aad);
        }
        catch (Exception ex)
        {
            LanLogger.Error("Relay", $"retry encrypt failed msgId={messageId}", ex);
            _relayOutboxInFlight.Remove(messageId);
            return;
        }

        LanLogger.Info("Relay", $"store msgId={messageId} peer={msg.PeerPublicKeyB64[..Math.Min(8, msg.PeerPublicKeyB64.Length)]} — uploading to cloud relay mailbox");
        Task.Run(async () =>
        {
            var confirmed = await RelayClient.Shared.StoreAsync(
                peerRelayIdHash, messageId, encrypted.ctB64, encrypted.nonceB64, msg.Timestamp);
            Dispatch(() =>
            {
                _relayOutboxInFlight.Remove(messageId);
                if (!confirmed)
                {
                    LanLogger.Warn("Relay", $"store msgId={messageId} not confirmed by Worker — will retry");
                    return;
                }
                _relayOutboxLastTry.Remove(messageId);
                MarkRelayStored(messageId);
            });
        });
    }

    /// Called once the Worker has confirmed a store. Flips the persisted
    /// RelayStored flag (so the outbox retry loop stops retrying it), marks
    /// the history entry, and notifies the UI immediately.
    private void MarkRelayStored(string messageId)
    {
        var pending = ConfigStore.Shared.Config.PendingMessages.FirstOrDefault(m => m.MessageId == messageId);
        if (pending is not null)
        {
            pending.RelayStored = true;
            ConfigStore.Shared.Save();
        }
        HistoryStore.Shared.MarkRelayDelivery(messageId);
        HistoryStore.Shared.Save();
        OnDeliveryPathUpdate?.Invoke(messageId);
    }

    // MARK: - Handle relay-delivered messages (from cloud Worker)

    /// Decrypts and processes a message that arrived via the cloud relay.
    /// Call from AppModel after FetchPendingAsync().
    /// <summary>
    /// Filed under the sender's key. <paramref name="replyAddress"/> is only
    /// where a delivery receipt can go — the sender's live address, or "" when
    /// they are not on the LAN right now, in which case they simply don't get one.
    /// </summary>
    public void HandleRelayMessage(RelayPendingMessage msg, string replyAddress)
    {
        // Global dedup: the same message may already have arrived over the LAN,
        // and history written before conversations were filed by key can hold
        // it under an address-named bucket this key's thread never reads. If we
        // already have it, just clean up the mailbox so it doesn't linger for
        // the full TTL.
        if (HistoryStore.Shared.ContainsMessageId(msg.MessageId))
        {
            LanLogger.Info("Relay", $"duplicate relay msg {msg.MessageId} — already in history, deleting from mailbox only");
            _ = RelayClient.Shared.DeleteAsync(msg.MessageId);
            return;
        }

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
        var peer = msg.SenderPublicKeyB64;

        // A control record carries an edit or delete the sender made while we
        // were offline. It is never a chat message and must not become a bubble.
        var control = RelayControlEnvelope.Decode(text);
        if (control is not null)
        {
            ApplyRelayControl(control, peer);
            // Applied or refused, the record is spent: leaving it would replay
            // on every poll until the Worker's 72-hour TTL expires it.
            _ = RelayClient.Shared.DeleteAsync(msg.MessageId);
            return;
        }

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
        HistoryStore.Shared.Append(entry, peer);
        HistoryStore.Shared.Save();
        Dispatch(() => OnMessageReceived?.Invoke(peer, entry));
        LanLogger.Info("Relay", $"delivered relay msg {msg.MessageId} from {msg.SenderUsername} peer={peer[..Math.Min(8, peer.Length)]}");

        // Send sent_receipt so the sender sees "Delivered" for their relayed
        // message — only when they are on the LAN right now.
        if (!string.IsNullOrEmpty(replyAddress))
            SendReceipt("sent_receipt", msg.MessageId, replyAddress);

        // Delete from relay now that we've processed it (best-effort)
        _ = RelayClient.Shared.DeleteAsync(msg.MessageId);
    }

    private async Task<bool> FireTcpAsync(byte[] frame, string ip, int port, string description,
                                          string? peerKey = null)
    {
        // An empty address is a peer that is not on the LAN: fail at once, and
        // let the caller queue or relay.
        if (string.IsNullOrEmpty(ip)) return false;
        // Two attempts with a short pause. A single SYN lost to Wi-Fi power
        // save or a peer's listener mid-rebuild is common on real LANs; without
        // the retry, one lost packet turns into a "Queued" message even though
        // the peer is online. Retrying is safe: a failed attempt either never
        // connected or delivered a partial frame, which the receiver discards.
        if (await FireTcpOnceAsync(frame, ip, port, description).ConfigureAwait(false))
        {
            if (peerKey is not null) Dispatch(() => OnPeerReachable?.Invoke(peerKey, ip));
            return true;
        }
        await Task.Delay(300).ConfigureAwait(false);
        if (await FireTcpOnceAsync(frame, ip, port, $"{description} (retry)").ConfigureAwait(false))
        {
            if (peerKey is not null) Dispatch(() => OnPeerReachable?.Invoke(peerKey, ip));
            return true;
        }
        return false;
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
