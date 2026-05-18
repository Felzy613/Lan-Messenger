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
    public string?   PhotoB64         { get; init; }
    public string    LastMessage      { get; init; } = "";
    public DateTime? LastTimestamp    { get; init; }
    public int       UnreadCount      { get; init; }
    public bool      IsTyping         { get; init; }
    public string    TypingSender     { get; init; } = "";
    public bool      IsOnline         { get; init; }
    public bool      IsArchived       { get; init; }
}

// Root state object. Wires all services; single source of truth for the UI.
public sealed partial class AppModel : ObservableObject
{
    // MARK: - Published UI state
    [ObservableProperty] private Dictionary<string, PeerInfo>   _peers         = [];
    [ObservableProperty] private List<ConversationViewModel>     _conversations = [];
    [ObservableProperty] private List<ConversationViewModel>     _archivedConversations = [];
    [ObservableProperty] private string?                         _selectedPeerIP;
    [ObservableProperty] private Dictionary<string, List<MessageEntry>> _messages = [];
    [ObservableProperty] private Dictionary<string, (string Sender, bool Active)> _typingStates = [];
    [ObservableProperty] private Dictionary<string, (string Label, long Bytes, long Total)> _activeTransfers = [];
    [ObservableProperty] private bool                            _showMigrationPrompt;
    [ObservableProperty] private string?                         _pendingImportKeyB64;
    [ObservableProperty] private UpdateInfo?                     _availableUpdate;
    [ObservableProperty] private UpdateProgress                  _updateProgress = new(UpdateProgressState.Idle);

    public readonly NetworkCoordinator Coordinator = new();
    private DispatcherQueue _dq;
    private DispatcherTimer? _peerTimeoutTimer;
    private DispatcherTimer? _updateCheckTimer;

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
        ScheduleAutoUpdateCheck();
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
        // Last-resort self-suppression — defends against stale `OwnIPs` snapshots in
        // DiscoveryService when the machine's network interfaces change after start.
        if (string.IsNullOrEmpty(publicKeyB64) ||
            publicKeyB64 == KeyManager.Shared.PublicKeyB64) return;
        if (GetLocalIPAddresses().Contains(ip)) return;

        // If we have a saved contact for this device ID whose IP has changed,
        // migrate the conversation history so the user doesn't lose context.
        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == publicKeyB64);
        if (contact is not null && contact.LastIP != ip)
        {
            var oldIP = contact.LastIP;
            var msgs = new Dictionary<string, List<MessageEntry>>(Messages);
            if (msgs.Remove(oldIP, out var oldList))
            {
                if (!msgs.TryGetValue(ip, out var curList)) curList = msgs[ip] = [];
                curList.AddRange(oldList);
                curList.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            }
            Messages = msgs;

            HistoryStore.Shared.Migrate(oldIP, ip);
            HistoryStore.Shared.Save();
            contact.LastIP = ip;
            var arch = ConfigStore.Shared.Config.ArchivedConversations;
            for (var i = 0; i < arch.Count; i++) if (arch[i] == oldIP) arch[i] = ip;
            var hid = ConfigStore.Shared.Config.HiddenConversations;
            for (var i = 0; i < hid.Count; i++) if (hid[i] == oldIP) hid[i] = ip;
            ConfigStore.Shared.Save();
            if (SelectedPeerIP == oldIP) SelectedPeerIP = ip;
        }

        var current = Peers;
        var updated = new Dictionary<string, PeerInfo>(current);
        if (updated.TryGetValue(publicKeyB64, out var existing))
        {
            if (existing.IP == ip)
            {
                // Same IP — just bump the heartbeat timestamp; no structural change.
                // The 2-second timeout timer handles online/offline UI transitions.
                existing.LastSeen = DateTime.UtcNow;
                return;
            }
            // IP changed — fall through to replace the entry below.
        }
        updated[publicKeyB64] = new PeerInfo
        {
            IP = ip, Username = username, Port = port,
            PublicKeyB64 = publicKeyB64, LastSeen = DateTime.UtcNow,
        };
        Peers = updated;
        RefreshConversations();
        MessagingService.Shared.DeliverPending(ip, publicKeyB64);
        DeliverPendingFiles(ip, publicKeyB64);
    }

    private void StartPeerTimeoutTimer()
    {
        // Keep peers in the dictionary even when they go offline — their public key
        // is needed to queue outgoing messages for delivery when they reconnect.
        // IsOnline is computed from LastSeen, so the UI still shows them as offline.
        _peerTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        var _lastOnlineSet = new HashSet<string>();
        _peerTimeoutTimer.Tick += (_, _) =>
        {
            var nowOnline = Peers.Keys.Where(k => Peers[k].IsOnline).ToHashSet();
            if (!nowOnline.SetEquals(_lastOnlineSet))
            {
                _lastOnlineSet = nowOnline;
                RefreshConversations();
            }
            // Refresh OwnIPs so the self-detection filter survives DHCP/interface changes.
            Coordinator.Discovery.OwnIPs = [..GetLocalIPAddresses()];
        };
        _peerTimeoutTimer.Start();
    }

    // MARK: - Conversations

    private void RefreshConversations()
    {
        // Threads only exist for saved contacts (or IPs we have history with) —
        // random discovered peers must not auto-appear as conversations.
        // `HiddenConversations` covers threads the user deleted; the contact
        // stays saved so the user can re-open the thread from "New message".
        var hidden   = ConfigStore.Shared.Config.HiddenConversations.ToHashSet();
        var archived = ConfigStore.Shared.Config.ArchivedConversations.ToHashSet();
        var active   = new List<ConversationViewModel>();
        var arch     = new List<ConversationViewModel>();
        var seenIPs  = new HashSet<string>();

        // Saved contacts — include whether currently online or offline.
        foreach (var contact in ConfigStore.Shared.Config.Contacts)
        {
            var onlinePeer = Peers.Values.FirstOrDefault(p =>
                p.PublicKeyB64 == contact.PublicKeyB64 && p.IsOnline);
            var ip = onlinePeer?.IP ?? contact.LastIP;
            if (hidden.Contains(ip) || hidden.Contains(contact.LastIP)) continue;
            if (seenIPs.Contains(ip)) continue;
            seenIPs.Add(ip);
            var entries = Messages.TryGetValue(ip, out var list) ? list : [];
            var last    = entries.Count > 0 ? entries[^1] : null;
            var typing  = TypingStates.TryGetValue(ip, out var t) ? t : default;
            var vm = new ConversationViewModel
            {
                PeerIP           = ip,
                PeerName         = contact.Username,
                PeerPublicKeyB64 = contact.PublicKeyB64,
                PhotoB64         = contact.PhotoB64,
                LastMessage      = LastMessagePreview(entries),
                LastTimestamp    = last is not null
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(last.Timestamp * 1000)).UtcDateTime
                    : null,
                UnreadCount  = CountUnread(entries),
                IsTyping     = typing.Active,
                TypingSender = typing.Sender ?? "",
                IsOnline     = onlinePeer is not null,
                IsArchived   = archived.Contains(ip),
            };
            (vm.IsArchived ? arch : active).Add(vm);
        }

        // Any IPs we have message history with but no contact entry —
        // e.g. someone messaged us once and isn't saved. Don't lose those.
        foreach (var (ip, entries) in Messages)
        {
            if (hidden.Contains(ip) || seenIPs.Contains(ip) || entries.Count == 0) continue;
            var last = entries[^1];
            var name = entries.LastOrDefault(e => e.Incoming)?.Sender ?? ip;
            var onlinePeer = Peers.Values.FirstOrDefault(p => p.IP == ip && p.IsOnline);
            var vm = new ConversationViewModel
            {
                PeerIP           = ip,
                PeerName         = name,
                PeerPublicKeyB64 = onlinePeer?.PublicKeyB64 ?? "",
                LastMessage      = LastMessagePreview(entries),
                LastTimestamp    = DateTimeOffset.FromUnixTimeMilliseconds((long)(last.Timestamp * 1000)).UtcDateTime,
                UnreadCount      = CountUnread(entries),
                IsTyping         = false,
                IsOnline         = onlinePeer is not null,
                IsArchived       = archived.Contains(ip),
            };
            (vm.IsArchived ? arch : active).Add(vm);
        }

        active.Sort((a, b) =>
            (b.LastTimestamp ?? DateTime.MinValue).CompareTo(a.LastTimestamp ?? DateTime.MinValue));
        arch.Sort((a, b) =>
            (b.LastTimestamp ?? DateTime.MinValue).CompareTo(a.LastTimestamp ?? DateTime.MinValue));
        Conversations = active;
        ArchivedConversations = arch;
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
            OnPropertyChanged(nameof(Messages));
            RefreshConversations();
        }
    }

    // Queue or send a file. If the peer is offline, the path is persisted in
    // config and retried whenever the peer comes back online.
    public void SendFile(string filePath, string peerIP)
    {
        var onlinePeer = PeerByIP(peerIP);
        var publicKey = onlinePeer?.PublicKeyB64
            ?? ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.LastIP == peerIP)?.PublicKeyB64;
        if (publicKey is null) return;

        if (onlinePeer is not null)
        {
            FileTransferService.Shared.Enqueue(filePath, peerIP, publicKey);
            return;
        }

        // Offline: persist for later, add a "Queued" outgoing bubble so the user sees the file.
        var username = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == publicKey)?.Username ?? "Unknown";
        ConfigStore.Shared.Config.PendingFiles.Add(new PendingFileConfig
        {
            FilePath         = filePath,
            PeerPublicKeyB64 = publicKey,
            PeerUsername     = username,
            Timestamp        = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        });
        ConfigStore.Shared.Save();

        var entry = new MessageEntry
        {
            Sender = ConfigStore.Shared.Config.Username,
            Text = $"__FILE__:{filePath}",
            Incoming = false,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            MessageId = null,
            Status = "Queued",
            ReadReceiptSent = false,
        };
        HistoryStore.Shared.Append(entry, peerIP);
        HistoryStore.Shared.Save();
        var msgs = new Dictionary<string, List<MessageEntry>>(Messages);
        if (!msgs.TryGetValue(peerIP, out var l)) l = msgs[peerIP] = [];
        l.Add(entry);
        Messages = msgs;
        RefreshConversations();
    }

    private void DeliverPendingFiles(string peerIP, string peerPublicKeyB64)
    {
        // 1) Re-trigger any in-memory queue that stalled on an earlier failed attempt.
        FileTransferService.Shared.RetryQueue(peerIP, peerPublicKeyB64);

        // 2) Drain the persistent pending-file queue for this peer.
        var matching = ConfigStore.Shared.Config.PendingFiles
            .Where(f => f.PeerPublicKeyB64 == peerPublicKeyB64).ToList();
        if (matching.Count == 0) return;

        foreach (var item in matching)
        {
            if (!File.Exists(item.FilePath)) continue;
            FileTransferService.Shared.Enqueue(item.FilePath, peerIP, peerPublicKeyB64);
        }

        ConfigStore.Shared.Config.PendingFiles.RemoveAll(f => f.PeerPublicKeyB64 == peerPublicKeyB64);
        ConfigStore.Shared.Save();
    }

    // MARK: - Conversation / contact actions

    public void ArchiveConversation(string peerIP)
    {
        if (!ConfigStore.Shared.Config.ArchivedConversations.Contains(peerIP))
        {
            ConfigStore.Shared.Config.ArchivedConversations.Add(peerIP);
            ConfigStore.Shared.Save();
        }
        if (SelectedPeerIP == peerIP) SelectedPeerIP = null;
        RefreshConversations();
    }

    public void UnarchiveConversation(string peerIP)
    {
        ConfigStore.Shared.Config.ArchivedConversations.RemoveAll(x => x == peerIP);
        ConfigStore.Shared.Save();
        RefreshConversations();
    }

    // Deletes a conversation: removes message history and hides the thread from the
    // sidebar. The contact stays in the saved contacts list — re-open the thread
    // through the "New message" picker.
    public void DeleteConversation(string peerIP)
    {
        var msgs = new Dictionary<string, List<MessageEntry>>(Messages);
        msgs.Remove(peerIP);
        Messages = msgs;
        HistoryStore.Shared.Delete(peerIP);
        HistoryStore.Shared.Save();
        if (!ConfigStore.Shared.Config.HiddenConversations.Contains(peerIP))
            ConfigStore.Shared.Config.HiddenConversations.Add(peerIP);
        ConfigStore.Shared.Config.ArchivedConversations.RemoveAll(x => x == peerIP);
        ConfigStore.Shared.Save();
        if (SelectedPeerIP == peerIP) SelectedPeerIP = null;
        RefreshConversations();
    }

    // Unhide a contact's thread and select it so the user can chat with them.
    public void StartConversation(string publicKeyB64)
    {
        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == publicKeyB64);
        if (contact is null) return;
        var onlinePeer = Peers.Values.FirstOrDefault(p => p.PublicKeyB64 == publicKeyB64);
        var ip = onlinePeer?.IP ?? contact.LastIP;
        ConfigStore.Shared.Config.HiddenConversations.RemoveAll(h => h == ip || h == contact.LastIP);
        ConfigStore.Shared.Save();
        RefreshConversations();
        SelectedPeerIP = ip;
    }

    public void DeleteContact(string publicKeyB64)
    {
        var removed = ConfigStore.Shared.Config.Contacts.Where(c => c.PublicKeyB64 == publicKeyB64).ToList();
        ConfigStore.Shared.Config.Contacts.RemoveAll(c => c.PublicKeyB64 == publicKeyB64);
        ConfigStore.Shared.Save();
        foreach (var c in removed) DeleteConversation(c.LastIP);
    }

    public void UpdateContact(string publicKeyB64, string username, string? photoB64)
    {
        var c = ConfigStore.Shared.Config.Contacts.FirstOrDefault(x => x.PublicKeyB64 == publicKeyB64);
        if (c is null) return;
        c.Username = username;
        c.PhotoB64 = photoB64;
        ConfigStore.Shared.Save();
        RefreshConversations();
    }

    public void AddContact(string publicKeyB64, string username, string lastIP, string? photoB64 = null)
    {
        if (ConfigStore.Shared.Config.Contacts.Any(c => c.PublicKeyB64 == publicKeyB64)) return;
        ConfigStore.Shared.Config.Contacts.Add(new ContactConfig
        {
            PublicKeyB64 = publicKeyB64,
            Username     = username,
            LastIP       = lastIP,
            PhotoB64     = photoB64,
        });
        ConfigStore.Shared.Save();
        RefreshConversations();
    }

    // MARK: - Updates

    private void ScheduleAutoUpdateCheck()
    {
        // Initial check shortly after launch, then every 6 hours.
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            await CheckForUpdatesAsync(silent: true);
        });
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateCheckTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(silent: true);
        _updateCheckTimer.Start();
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync(bool silent)
    {
        var info = await UpdateService.Shared.CheckAsync(ConfigStore.Shared.Config.UpdateRepo);
        ConfigStore.Shared.Config.LastUpdateCheck = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ConfigStore.Shared.Save();
        _dq.TryEnqueue(() =>
        {
            if (info is not null) AvailableUpdate = info;
            else if (silent) AvailableUpdate = null;
        });
        return info;
    }

    public void InstallUpdate()
    {
        var info = AvailableUpdate;
        if (info is null) return;
        UpdateProgress = new(UpdateProgressState.Downloading, 0);
        Task.Run(async () =>
        {
            await UpdateService.Shared.DownloadAndInstallAsync(info, p =>
            {
                _dq.TryEnqueue(() => UpdateProgress = p);
            });
        });
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
            // Incoming message from a previously-deleted thread should resurface it.
            if (ConfigStore.Shared.Config.HiddenConversations.Contains(ip))
            {
                ConfigStore.Shared.Config.HiddenConversations.RemoveAll(h => h == ip);
                ConfigStore.Shared.Save();
            }
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
        // Use new List<> to break the reference shared with HistoryStore — otherwise
        // OnMessageReceived would add entries twice (once via HistoryStore.Append, once
        // via list.Add), showing every message twice in the chat.
        Messages = HistoryStore.Shared.History
            .ToDictionary(kv => kv.Key, kv => new List<MessageEntry>(kv.Value));
        RefreshConversations();
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
