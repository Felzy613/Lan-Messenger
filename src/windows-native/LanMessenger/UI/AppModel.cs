using CommunityToolkit.Mvvm.ComponentModel;
using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking;
using LanMessenger.Core.Persistence;
using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanMessenger.UI;

// Represents a discovered or saved peer.
public sealed class PeerInfo
{
    public string   IP           { get; init; } = "";
    public string   Username     { get; init; } = "";
    public int      Port         { get; init; }
    public string   PublicKeyB64 { get; init; } = "";
    public DateTime LastSeen     { get; set; }
    public bool     IsOnline     => (DateTime.UtcNow - LastSeen).TotalSeconds < 7;
}

// View model for one conversation row in the sidebar.
public sealed class ConversationViewModel
{
    public string    PeerIP           { get; init; } = "";
    public string    PeerName         { get; init; } = "";
    public string    PeerPublicKeyB64 { get; init; } = "";
    public string    LastMessage      { get; init; } = "";
    public DateTime? LastTimestamp    { get; init; }
    public int       UnreadCount      { get; init; }
    public bool      IsTyping         { get; init; }
    public string    TypingSender     { get; init; } = "";
    public bool      IsOnline         { get; init; }
}

// Root state object. Wires all services; single source of truth for the UI.
public sealed partial class AppModel : ObservableObject
{
    // MARK: - Published UI state
    [ObservableProperty] private Dictionary<string, PeerInfo>   _peers         = [];
    [ObservableProperty] private List<ConversationViewModel>     _conversations = [];
    [ObservableProperty] private string?                         _selectedPeerIP;
    [ObservableProperty] private Dictionary<string, List<MessageEntry>> _messages = [];
    [ObservableProperty] private Dictionary<string, (string Sender, bool Active)> _typingStates = [];
    [ObservableProperty] private Dictionary<string, (string Label, long Bytes, long Total)> _activeTransfers = [];
    [ObservableProperty] private bool                            _showMigrationPrompt;
    [ObservableProperty] private string?                         _pendingImportKeyB64;

    public readonly NetworkCoordinator Coordinator = new();
    private DispatcherQueue _dq;
    private DispatcherTimer? _peerTimeoutTimer;

    public AppModel(DispatcherQueue dq)
    {
        _dq = dq;
        WireDelegates();
        Start();
    }

    // MARK: - Start

    private void Start()
    {
        var localIPs = GetLocalIPAddresses();
        try
        {
            Coordinator.Start(ConfigStore.Shared.Config.Username, [..localIPs], _dq);
        }
        catch (Exception ex)
        {
            // Port conflict or network error — app still opens, just without discovery/messaging.
            System.Diagnostics.Debug.WriteLine($"[AppModel] Network start failed: {ex.Message}");
            throw new InvalidOperationException(
                $"Could not bind network ports (54231/54232).\n\n" +
                $"Another instance may already be running, or a firewall is blocking the ports.\n\n" +
                $"Error: {ex.Message}", ex);
        }
        NotificationService.Shared.Register();
        LoadHistory();
        StartPeerTimeoutTimer();
        CheckMigration();
    }

    // MARK: - Migration

    private void CheckMigration()
    {
        if (!ConfigStore.Shared.NeedsMigration) return;
        var keyB64 = ConfigStore.Shared.ImportPythonConfig();
        PendingImportKeyB64 = keyB64;
        ShowMigrationPrompt = true;
    }

    public void AcceptMigrationWithExistingKey()
    {
        if (PendingImportKeyB64 is not null)
            KeyManager.Shared.ImportFromBase64(PendingImportKeyB64);
        ShowMigrationPrompt = false;
        PendingImportKeyB64 = null;
        LoadHistory();
    }

    public void AcceptMigrationWithFreshKey()
    {
        ShowMigrationPrompt = false;
        PendingImportKeyB64 = null;
    }

    // MARK: - Peers

    private void UpsertPeer(string ip, string username, int port, string publicKeyB64)
    {
        var current = Peers;
        var updated = new Dictionary<string, PeerInfo>(current);
        if (updated.TryGetValue(publicKeyB64, out var existing))
        {
            existing.LastSeen = DateTime.UtcNow;
        }
        else
        {
            updated[publicKeyB64] = new PeerInfo
            {
                IP = ip, Username = username, Port = port,
                PublicKeyB64 = publicKeyB64, LastSeen = DateTime.UtcNow,
            };
        }
        Peers = updated;
        RefreshConversations();
        MessagingService.Shared.DeliverPending(ip, publicKeyB64);
    }

    private void StartPeerTimeoutTimer()
    {
        _peerTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _peerTimeoutTimer.Tick += (_, _) =>
        {
            var before = Peers.Count;
            var filtered = Peers.Where(kv => kv.Value.IsOnline)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (filtered.Count != before) { Peers = filtered; RefreshConversations(); }
        };
        _peerTimeoutTimer.Start();
    }

    // MARK: - Conversations

    private void RefreshConversations()
    {
        var hidden = ConfigStore.Shared.Config.HiddenConversations.ToHashSet();
        var result = new List<ConversationViewModel>();
        var seenIPs = new HashSet<string>();

        // Online peers first.
        foreach (var (keyB64, peer) in Peers)
        {
            if (hidden.Contains(peer.IP)) continue;
            seenIPs.Add(peer.IP);
            var entries = Messages.TryGetValue(peer.IP, out var list) ? list : [];
            var last    = entries.Count > 0 ? entries[^1] : null;
            var typing  = TypingStates.TryGetValue(peer.IP, out var t) ? t : default;
            result.Add(new ConversationViewModel
            {
                PeerIP           = peer.IP,
                PeerName         = peer.Username,
                PeerPublicKeyB64 = keyB64,
                LastMessage      = LastMessagePreview(entries),
                LastTimestamp    = last is not null
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(last.Timestamp * 1000)).UtcDateTime
                    : null,
                UnreadCount  = CountUnread(entries),
                IsTyping     = typing.Active,
                TypingSender = typing.Sender ?? "",
                IsOnline     = true,
            });
        }

        // Saved contacts that are currently offline.
        foreach (var contact in ConfigStore.Shared.Config.Contacts)
        {
            if (hidden.Contains(contact.LastIP)) continue;
            if (seenIPs.Contains(contact.LastIP)) continue;
            seenIPs.Add(contact.LastIP);
            var entries = Messages.TryGetValue(contact.LastIP, out var list) ? list : [];
            var last    = entries.Count > 0 ? entries[^1] : null;
            result.Add(new ConversationViewModel
            {
                PeerIP           = contact.LastIP,
                PeerName         = contact.Username,
                PeerPublicKeyB64 = contact.PublicKeyB64,
                LastMessage      = LastMessagePreview(entries),
                LastTimestamp    = last is not null
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(last.Timestamp * 1000)).UtcDateTime
                    : null,
                UnreadCount = CountUnread(entries),
                IsTyping    = false,
                IsOnline    = false,
            });
        }

        // Any IPs we have message history with but no peer / no contact —
        // still show them so the conversation doesn't vanish when the peer goes offline.
        foreach (var (ip, entries) in Messages)
        {
            if (hidden.Contains(ip) || seenIPs.Contains(ip) || entries.Count == 0) continue;
            var last = entries[^1];
            var name = entries.LastOrDefault(e => e.Incoming)?.Sender ?? ip;
            result.Add(new ConversationViewModel
            {
                PeerIP           = ip,
                PeerName         = name,
                PeerPublicKeyB64 = "",
                LastMessage      = LastMessagePreview(entries),
                LastTimestamp    = DateTimeOffset.FromUnixTimeMilliseconds((long)(last.Timestamp * 1000)).UtcDateTime,
                UnreadCount      = CountUnread(entries),
                IsTyping         = false,
                IsOnline         = false,
            });
        }

        result.Sort((a, b) =>
            (b.LastTimestamp ?? DateTime.MinValue).CompareTo(a.LastTimestamp ?? DateTime.MinValue));
        Conversations = result;
    }

    private static int CountUnread(IReadOnlyList<MessageEntry> entries)
        => entries.Count(e => e.Incoming && !e.ReadReceiptSent);

    private static string LastMessagePreview(IReadOnlyList<MessageEntry> entries)
    {
        if (entries.Count == 0) return "";
        var last = entries[^1];
        if (last.Text.StartsWith("__FILE__:"))
        {
            var path = last.Text["__FILE__:".Length..];
            return "📎 " + Path.GetFileName(path);
        }
        return last.Text;
    }

    // MARK: - Messaging

    public void SendMessage(string text, string peerIP, MessageEntry? replyTo = null)
    {
        // Find the public key either from currently-online peers, or fall back to saved contacts
        // (so we can still queue messages to offline contacts).
        var publicKey = PeerByIP(peerIP)?.PublicKeyB64
            ?? ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.LastIP == peerIP)?.PublicKeyB64;
        if (publicKey is null) return;
        MessagingService.Shared.SendText(text, peerIP, publicKey, replyTo);
    }

    public void SendTyping(bool active, string peerIP)
    {
        var peer = PeerByIP(peerIP);
        if (peer is null) return;
        MessagingService.Shared.SendTyping(active, peerIP, peer.PublicKeyB64);
    }

    public void SendReadReceipt(MessageEntry entry, string peerIP)
    {
        if (!entry.Incoming || entry.MessageId is null || entry.ReadReceiptSent) return;
        MarkConversationRead(peerIP);
    }

    // Marks every incoming unread message for the given peer as read, sends read
    // receipts, and updates the in-memory `Messages` so the unread badge clears.
    public void MarkConversationRead(string peerIP)
    {
        if (!Messages.TryGetValue(peerIP, out var list) || list.Count == 0) return;
        var anyChanged = false;
        foreach (var e in list)
        {
            if (!e.Incoming || e.ReadReceiptSent) continue;
            if (e.MessageId is { } id)
            {
                MessagingService.Shared.SendReceipt("read_receipt", id, peerIP);
                HistoryStore.Shared.MarkReadReceiptSent(id, peerIP);
            }
            e.ReadReceiptSent = true;
            anyChanged = true;
        }
        if (anyChanged)
        {
            HistoryStore.Shared.Save();
            // Trigger UI updates — Messages dict is the same reference but we want listeners to refresh.
            OnPropertyChanged(nameof(Messages));
            RefreshConversations();
        }
    }

    public void SendFile(string filePath, string peerIP)
    {
        var peer = PeerByIP(peerIP);
        if (peer is null) return;
        FileTransferService.Shared.Enqueue(filePath, peerIP, peer.PublicKeyB64);
    }

    // MARK: - Delegate wiring

    private void WireDelegates()
    {
        MessagingService.Shared.SetDispatcherQueue(_dq);
        FileTransferService.Shared.SetDispatcherQueue(_dq);

        Coordinator.PacketReceived += pkt =>
        {
            switch (pkt)
            {
                case ValidatedText or ValidatedTyping or ValidatedReceipt:
                    MessagingService.Shared.HandlePacket(pkt); break;
                case ValidatedFileStart or ValidatedFileChunk or ValidatedFileEnd:
                    FileTransferService.Shared.HandlePacket(pkt); break;
                case ValidatedDiscovery vd:
                    UpsertPeer(vd.SenderIP, vd.Packet.Username, vd.Packet.Port, vd.Packet.PublicKeyB64);
                    break;
            }
        };

        Coordinator.PeerDiscovered += (pkt, ip) =>
            UpsertPeer(ip, pkt.Username, pkt.Port, pkt.PublicKeyB64);

        MessagingService.Shared.OnMessageReceived = (ip, entry) =>
        {
            var updated = new Dictionary<string, List<MessageEntry>>(Messages);
            if (!updated.TryGetValue(ip, out var list)) list = updated[ip] = [];
            list.Add(entry);
            Messages = updated;
            RefreshConversations();
            if (SelectedPeerIP != ip)
                NotificationService.Shared.ShowMessage(entry.Sender, entry.Text);
        };

        MessagingService.Shared.OnStatusUpdate = (ip, msgId, status) =>
        {
            if (!Messages.TryGetValue(ip, out var list)) return;
            foreach (var e in list.Where(e => e.MessageId == msgId)) e.Status = status;
            OnPropertyChanged(nameof(Messages));
        };

        MessagingService.Shared.OnTypingUpdate = (ip, sender, active) =>
        {
            var updated = new Dictionary<string, (string, bool)>(TypingStates)
            {
                [ip] = (sender, active)
            };
            TypingStates = updated;
            RefreshConversations();
        };

        FileTransferService.Shared.OnProgress = (ip, label, bytes, total) =>
        {
            var updated = new Dictionary<string, (string, long, long)>(ActiveTransfers)
            {
                [ip] = (label, bytes, total)
            };
            ActiveTransfers = updated;
        };

        FileTransferService.Shared.OnComplete = (ip, label, localPath) =>
        {
            var updated = new Dictionary<string, (string, long, long)>(ActiveTransfers);
            updated.Remove(ip);
            ActiveTransfers = updated;

            // Sender side gets a non-null local path — add an outgoing file bubble.
            if (localPath is null) return;
            var entry = new MessageEntry
            {
                Sender = ConfigStore.Shared.Config.Username,
                Text = $"__FILE__:{localPath}",
                Incoming = false,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                MessageId = null, Status = "Sent", ReadReceiptSent = false,
            };
            HistoryStore.Shared.Append(entry, ip);
            HistoryStore.Shared.Save();
            var msgs = new Dictionary<string, List<MessageEntry>>(Messages);
            if (!msgs.TryGetValue(ip, out var l)) l = msgs[ip] = [];
            l.Add(entry);
            Messages = msgs;
            RefreshConversations();
        };

        FileTransferService.Shared.OnIncomingFile = (ip, sender, path) =>
        {
            NotificationService.Shared.ShowFileReceived(sender, Path.GetFileName(path));
            var entry = new MessageEntry
            {
                Sender = sender, Text = $"__FILE__:{path}", Incoming = true,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                MessageId = null, Status = "", ReadReceiptSent = false,
            };
            HistoryStore.Shared.Append(entry, ip);
            HistoryStore.Shared.Save();
            var updated = new Dictionary<string, List<MessageEntry>>(Messages);
            if (!updated.TryGetValue(ip, out var list)) list = updated[ip] = [];
            list.Add(entry);
            Messages = updated;
            RefreshConversations();
        };
    }

    // MARK: - Helpers

    private void LoadHistory()
    {
        Messages = HistoryStore.Shared.History
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private PeerInfo? PeerByIP(string ip) => Peers.Values.FirstOrDefault(p => p.IP == ip);

    private static List<string> GetLocalIPAddresses()
    {
        var result = new List<string>();
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                    result.Add(addr.Address.ToString());
            }
        }
        return result;
    }
}
