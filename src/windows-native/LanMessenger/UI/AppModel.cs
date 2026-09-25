using CommunityToolkit.Mvvm.ComponentModel;
using LanMessenger.Core.Crypto;
using LanMessenger.Core.Networking;
using LanMessenger.Core.Networking.Media;
using LanMessenger.Core.Persistence;
using LanMessenger.Core.Protocol;
using LanMessenger.Core.Services;
using LanMessenger.UI.RemoteDesktop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LanMessenger.UI;

// Represents a discovered or saved peer.
public sealed class PeerInfo
{
    public string   IP           { get; init; } = "";
    public string   Username     { get; init; } = "";
    public int      Port         { get; init; }
    public string   PublicKeyB64 { get; init; } = "";
    public DateTime LastSeen     { get; set; }
    // Explicit, authoritative presence. Set by the presence evaluator, by
    // heartbeats (discovery/TCP), and by goodbye/network-loss — never inferred
    // from LastSeen at read time, so the UI updates reactively and Windows and
    // macOS agree. See PresenceEvaluator.
    public PeerPresence Presence { get; set; } = PeerPresence.Online;
    // Every IP this peer has advertised (from discovery `ips`), used as probe
    // targets so a multi-homed or roaming peer can still be reconfirmed.
    public List<string> KnownIPs { get; set; } = [];
    // Capability tokens from the peer's last discovery packet. Empty means the
    // peer advertised none — which includes every client older than this field,
    // so absence must never be read as "probably supports it".
    public List<string> Caps { get; set; } = [];
    public bool     IsOnline     => Presence == PeerPresence.Online;
    // Whether remote desktop may even be offered for this peer. A peer that does
    // not advertise it drops remote_invite silently, so the menu item is
    // disabled rather than left to time out.
    public bool SupportsRemoteDesktop => Caps.Contains(ProtocolCapability.RemoteDesktopV1);
}

// View model for one conversation row in the sidebar.
public sealed class ConversationViewModel
{
    /// <summary>
    /// The conversation's identity (see <see cref="PeerId"/>): the peer's key, or
    /// <c>ip:&lt;address&gt;</c> for history from before keys were the filing name
    /// that no single contact could be matched to.
    /// </summary>
    public string    ConversationId   { get; init; } = "";
    public string    PeerName         { get; init; } = "";
    /// <summary>The key to encrypt to — equal to ConversationId, or "" for a legacy thread.</summary>
    public string    PeerPublicKeyB64 { get; init; } = "";
    public string?   PhotoB64         { get; init; }
    public string    LastMessage      { get; init; } = "";
    public DateTime? LastTimestamp    { get; init; }
    public int       UnreadCount      { get; init; }
    public bool      IsTyping         { get; init; }
    public string    TypingSender     { get; init; } = "";
    public bool      IsOnline         { get; init; }
    public bool      IsArchived       { get; init; }
    /// <summary>
    /// A thread with no key behind it. It can be read and deleted, not replied
    /// to: there is nobody it can safely be encrypted to.
    /// </summary>
    public bool      IsLegacy         => PeerId.IsLegacy(ConversationId);
}

// Root state object. Wires all services; single source of truth for the UI.
public sealed partial class AppModel : ObservableObject
{
    // MARK: - Published UI state
    [ObservableProperty] private Dictionary<string, PeerInfo>   _peers         = [];
    [ObservableProperty] private List<ConversationViewModel>     _conversations = [];
    [ObservableProperty] private List<ConversationViewModel>     _archivedConversations = [];
    // Everything below is keyed by conversation id (PeerId) — the peer's
    // identity key — and never by address. An address is where a device is
    // this morning; DHCP gives the same numbers to other machines, so a thread
    // filed by address is eventually somebody else's. Where to *send* is looked
    // up at the moment of sending: LiveAddress(key).
    [ObservableProperty] private string?                         _selectedConversationId;
    [ObservableProperty] private Dictionary<string, List<MessageEntry>> _messages = [];
    [ObservableProperty] private Dictionary<string, (string Sender, bool Active)> _typingStates = [];
    [ObservableProperty] private Dictionary<string, (string Label, long Bytes, long Total)> _activeTransfers = [];
    [ObservableProperty] private bool                            _showMigrationPrompt;
    [ObservableProperty] private string?                         _pendingImportKeyB64;
    [ObservableProperty] private UpdateInfo?                     _availableUpdate;
    [ObservableProperty] private UpdateProgress                  _updateProgress = new(UpdateProgressState.Idle);
    [ObservableProperty] private bool                            _isLocalNetworkAvailable = true;
    [ObservableProperty] private int                             _totalUnreadCount;

    // In-memory per-peer draft text for the composer. Not persisted — switching
    // conversations without sending restores whatever was typed.
    public Dictionary<string, string> Drafts { get; } = new();

    // True while the main window is visible on screen; false when hidden to tray.
    // Read receipts are only auto-sent when the window is visible so that messages
    // arriving after the user hides the window are not silently marked read.
    public bool IsWindowVisible { get; set; } = true;

    // True while the main window is the focused foreground window. Combined with
    // IsWindowVisible to decide whether to show a toast: a notification for an
    // incoming message/file is only suppressed when the window is both open and
    // focused, so it still shows while minimized, hidden to tray, or in the
    // background behind another app.
    public bool IsWindowFocused { get; set; } = true;

    private bool ShouldShowNotification => !(IsWindowVisible && IsWindowFocused);

    // Fires when a single message's status changes (Sent / Delivered / Read).
    // ChatPage listens to update one row in place — far cheaper than firing
    // Messages PropertyChanged, which would re-evaluate every observer that
    // reads the whole dictionary.
    public event Action<string, string, string>? MessageStatusUpdated;

    // Fires once the cloud relay Worker confirms an outgoing message was
    // actually stored — not when the upload is merely attempted. Lets
    // ChatPage show the "via relay" badge promptly instead of only after the
    // recipient later retrieves the message (which is what a full history
    // reload previously depended on).
    public event Action<string>? MessageDeliveryPathUpdated;

    public readonly NetworkCoordinator Coordinator = new();
    private DispatcherQueue _dq;
    private DispatcherTimer? _peerTimeoutTimer;
    private DispatcherTimer? _updateCheckTimer;
    private DispatcherTimer? _relayPollTimer;
    // Suppresses overlapping relay polls when a previous fetch is still inflight.
    private bool _relayFetchInFlight;
    // Interval between routine relay polls. Short enough to feel responsive when
    // a peer just left the LAN; long enough not to thrash the Worker.
    private static readonly TimeSpan RelayPollInterval = TimeSpan.FromSeconds(30);

    // SHA256(relay_id) for each peer, populated from discovery packets.
    // Used to upload queued messages to the cloud relay mailbox of offline peers.
    private readonly Dictionary<string, string> _peerRelayIdHashes = [];   // keyed by peerPublicKeyB64

    // Bursts of incoming packets (a chatty room, an active file transfer, a peer
    // typing fast) can produce many RefreshConversations calls per frame. Each
    // one rebuilds the entire Conversations list and re-binds every sidebar row.
    // Coalesce them to one per dispatcher tick so the UI thread stays free.
    private bool _refreshConvosScheduled;

    public AppModel(DispatcherQueue dq)
    {
        _dq = dq;
        WireDelegates();
        Start();
    }

    // MARK: - Start

    /// <summary>The invite exchange, built from this model's own dependencies.</summary>
    /// <remarks>
    /// Lives here rather than on RemoteDesktopService because it needs the
    /// consent prompt, the peer list and the session; the service stays free of
    /// all three so AttachInbound can keep running on the socket thread.
    /// </remarks>
    public RemoteInviteCoordinator InviteCoordinator => _inviteCoordinator ??= BuildInviteCoordinator();
    private RemoteInviteCoordinator? _inviteCoordinator;

    /// <summary>
    /// What the contact strip shows about an invite we sent: waiting, declined,
    /// unreachable, timed out.
    /// </summary>
    /// <remarks>
    /// Computed since the invite exchange was written and, until now, bound to
    /// nothing — so every outcome looked the same as a dead button: a decline,
    /// a timeout, an address that reached nobody, and even an invite that was
    /// working and waiting for an answer.
    /// </remarks>
    [ObservableProperty] private string? _remoteInviteStatus;

    /// <summary>
    /// Whose conversation the status belongs to, by identity key. Shown only in
    /// that peer's header, so "Waiting for Ari…" never appears in the Dell's.
    /// </summary>
    [ObservableProperty] private string? _remoteInviteTargetKey;

    private CancellationTokenSource? _remoteInviteStatusClear;

    /// <summary>
    /// Sets the status, and lets anything final fade after a few seconds. The
    /// waiting message stays for as long as the wait does; a decline or a
    /// timeout is news once and then just noise in the header. UI thread only.
    /// </summary>
    private void ShowRemoteInviteStatus(string message)
    {
        _remoteInviteStatusClear?.Cancel();
        RemoteInviteStatus = string.IsNullOrEmpty(message) ? null : message;
        if (string.IsNullOrEmpty(message) || message.StartsWith("Waiting for", StringComparison.Ordinal))
            return;

        var cts = new CancellationTokenSource();
        _remoteInviteStatusClear = cts;
        _ = Task.Delay(TimeSpan.FromSeconds(6), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _dq.TryEnqueue(() => { if (!cts.IsCancellationRequested) RemoteInviteStatus = null; });
        }, TaskScheduler.Default);
    }

    /// <summary>Changes whenever a remote-desktop session starts or ends.</summary>
    /// <remarks>
    /// The contact-strip button's enabled state depends on it, and nothing else
    /// in the chat header changes when a session ends — so without this the
    /// button was computed once while a session was running and stayed greyed
    /// out for the rest of the app's life.
    /// </remarks>
    [ObservableProperty] private bool _remoteSessionRunning;

    /// <summary>
    /// The identity key of the device a conversation belongs to, or null for a
    /// legacy thread that has none. A conversation id already is the key; this
    /// exists so call sites say what they mean.
    /// </summary>
    public string? PeerKeyForConversation(string? conversationId) =>
        PeerId.IsKey(conversationId) ? conversationId : null;

    /// <summary>
    /// The live discovery record for a conversation's device, found by identity
    /// key. Null when the device is not currently known on the network, and
    /// always null for a legacy thread — asking "who is at this old address?"
    /// is how the wrong device's name and presence ended up in a header.
    /// </summary>
    public PeerInfo? PeerForConversation(string? conversationId) =>
        PeerKeyForConversation(conversationId) is { } key ? Peers.GetValueOrDefault(key) : null;

    /// <summary>
    /// The contact-strip button: asks the peer identified by
    /// <paramref name="peerKey"/> to share their screen. Judged against the same
    /// policy an inbound invite is, so the interface can never start something
    /// the gate would refuse.
    /// </summary>
    /// <remarks>
    /// <b>Addressed by identity key.</b> The invite goes to wherever discovery
    /// last saw that key — the same lookup that just decided it is online. It
    /// used to go to the conversation's remembered address, so an invite from the
    /// Mac's conversation went to an address Ari held by then; the Mac never saw
    /// it, and every click after that was refused behind it.
    /// </remarks>
    public void RequestRemoteDesktop(string peerKey)
    {
        var availability = RemoteDesktopAvailability(peerKey);
        var peer = Peers.GetValueOrDefault(peerKey);
        if (!availability.IsAvailable || peer is null || string.IsNullOrEmpty(peer.IP))
        {
            LanLogger.Remote("invite_blocked", peer: peerKey[..Math.Min(8, peerKey.Length)],
                             reason: availability.Reason.ToString());
            return;
        }
        RemoteInviteTargetKey = peerKey;
        InviteCoordinator.Invite(peerKey, peer.IP, peer.Username);
    }

    /// <summary>Whether the button should be offered, and why not when it should not.</summary>
    public RemoteInviteAvailability RemoteDesktopAvailability(string peerKey)
    {
        var peer = Peers.Values.FirstOrDefault(p => p.PublicKeyB64 == peerKey);
        bool isContact = ConfigStore.Shared.Config.Contacts.Any(c => c.PublicKeyB64 == peerKey);
        return RemoteDesktopPolicy.Availability(
            ConfigStore.Shared.Config.RemoteDesktopMode,
            new RemoteInviteTarget
            {
                IsSavedContact = isContact,
                IsOnline = peer?.IsOnline ?? false,
                AdvertisesRemoteDesktop = peer?.SupportsRemoteDesktop ?? false,
                HasSessionInFlight = RemoteDesktopController.Shared.IsRunning
                                     || InviteCoordinator.HasInviteInFlight,
            });
    }

    /// <summary>The tooltip. Same wording as the macOS build.</summary>
    public static string RemoteDesktopHint(RemoteInviteAvailability availability, string peerName)
    {
        if (availability.IsAvailable) return $"Ask {peerName} to share their screen";
        return availability.Reason switch
        {
            RemoteUnavailableReason.LocalFeatureOff =>
                "Turn this on in LAN Messenger Settings (the gear icon), under Remote Desktop",
            RemoteUnavailableReason.PeerNotAContact => $"{peerName} is not a saved contact",
            RemoteUnavailableReason.PeerOffline => $"{peerName} is offline",
            // The whole reason the `caps` discovery field exists: without it
            // this would be an invite that vanishes and a spinner forever.
            RemoteUnavailableReason.PeerLacksCapability =>
                $"{peerName}'s version does not support remote desktop",
            RemoteUnavailableReason.SessionInFlight => "A remote desktop session is already running",
            _ => "Screen sharing is not available with this contact",
        };
    }

    private RemoteInviteCoordinator BuildInviteCoordinator()
    {
        var coordinator = new RemoteInviteCoordinator(new RemoteInviteEnvironment
        {
            Send = (frame, ip) => Coordinator.Send(frame, ip),
            AttachOutbound = (ip, frame) => Coordinator.AttachOutbound(ip, frame),
            OwnPublicKeyB64 = () => KeyManager.Shared.PublicKeyB64,
            OwnUsername = () => ConfigStore.Shared.Config.Username,
            PrivateKey = () => KeyManager.Shared.PrivateKey,
            Mode = () => ConfigStore.Shared.Config.RemoteDesktopMode,
            // KnownContact is the policy layer's own shape, deliberately
            // narrower than the stored one: it carries the three fields the
            // trust decision uses and nothing a photo or a relay id could
            // influence.
            Contacts = () => ConfigStore.Shared.Config.Contacts
                .Select(c => new KnownContact(c.PublicKeyB64, c.Username, c.LastIP))
                .ToList(),
            HasLiveSession = () => RemoteDesktopController.Shared.IsRunning,
            Registry = () => RemoteDesktopService.Shared.Registry,
            PresentConsent = (request, onOutcome) =>
                RemoteDesktopController.Shared.PresentConsent(_dq, request, onOutcome),
            StartViewing = (peerName, peerIP, channel) =>
                RemoteDesktopController.Shared.StartViewing(
                    _dq, peerName, peerIP, (RemoteAttachedChannel)channel),
            ArmHosting = (sessionId, peerName, peerIP) =>
                RemoteDesktopController.Shared.ArmHosting(sessionId, peerName, peerIP),
        });
        coordinator.OnStateChange = message =>
            _dq.TryEnqueue(() => ShowRemoteInviteStatus(message));
        return coordinator;
    }

    /// <summary>Files an audit record into the conversation it belongs to.</summary>
    /// <remarks>
    /// On the UI thread, like every other history append. The thread is not
    /// resurfaced if the user had deleted it, matching the Mac: the record is
    /// kept, and it is there when the conversation is reopened.
    /// </remarks>
    private void RecordRemoteAudit(string peer, RemoteAuditRecord record, double timestamp)
    {
        var entry = record.HistoryEntry(timestamp);
        HistoryStore.Shared.Append(entry, peer);
        HistoryStore.Shared.Save();
        var msgs = new Dictionary<string, List<MessageEntry>>(Messages);
        if (!msgs.TryGetValue(peer, out var list)) list = msgs[peer] = [];
        list.Add(entry);
        Messages = msgs;
        RefreshConversations();
    }

    private void Start()
    {
        CryptoRuntimeDiagnostics.LogOnce();

        // The transport service is reachable from the socket thread and knows
        // nothing about consent or the interface; these two hooks are how the
        // exchange and the session reach back into the model.
        RemoteDesktopService.Shared.Invites = InviteCoordinator;
        RemoteDesktopService.Shared.OnHostAttached = (sessionId, media) =>
            RemoteDesktopController.Shared.BeginHosting(_dq, sessionId, media);

        // Wired once, on the controller, because a session can end from six
        // places — the Stop button, the kill switch, the workstation locking,
        // sleep, the watchdog, or the viewer window closing — and every one of
        // them has to tell the peer. Doing it at each call site is how five of
        // the six end up forgetting.
        RemoteDesktopController.Shared.AnnounceEnd = (sessionId, peerIP, reason) =>
            InviteCoordinator.SendEnd(sessionId, peerIP, reason);

        // The audit trail. Left unassigned, every record a session raised went
        // nowhere, and the thread never said that a screen had been shared. The
        // time is taken here, where the event happened, rather than when the
        // dispatcher gets to it; a SessionEnded raised from a teardown on the
        // socket thread would otherwise carry whatever the UI thread was busy with.
        RemoteDesktopController.Shared.AppendAudit = (peerKey, record) =>
        {
            var who = peerKey[..Math.Min(8, peerKey.Length)];
            LanLogger.Remote("audit", peer: who, reason: record.Summary);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (!_dq.TryEnqueue(() => RecordRemoteAudit(peerKey, record, timestamp)))
                LanLogger.Remote("error", peer: who,
                                 reason: "audit record dropped: the UI dispatcher is gone");
        };

        RemoteDesktopController.Shared.OnChanged = () =>
            _dq.TryEnqueue(() => RemoteSessionRunning = RemoteDesktopController.Shared.IsRunning);

        // First launch: replace the bare "User" default with the OS account
        // name so peers immediately see something meaningful instead of "User".
        if (ConfigStore.Shared.Config.Username == "User")
        {
            var fallback = Environment.UserName?.Trim() ?? "";
            if (fallback.Length > 0 && fallback != "User")
            {
                ConfigStore.Shared.Config.Username = fallback;
                ConfigStore.Shared.Save();
            }
        }
        try
        {
            Coordinator.Start(_dq);
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
        IsLocalNetworkAvailable = Coordinator.IsLocalNetworkAvailable;
        Coordinator.NetworkAvailabilityChanged += () =>
        {
            var available = Coordinator.IsLocalNetworkAvailable;
            var wasAvailable = IsLocalNetworkAvailable;
            if (IsLocalNetworkAvailable != available) IsLocalNetworkAvailable = available;
            if (!available)
            {
                // Our own LAN dropped — we can't see anyone, so don't keep showing
                // stale green dots. Beacons will revive real peers on return.
                MarkAllPeersOffline("network-down");
            }
            // When the LAN comes back after being unavailable, re-announce
            // ourselves and drain the relay mailbox immediately — pending
            // messages may have piled up on the Worker while we were unreachable.
            if (available && !wasAvailable)
            {
                LanLogger.Info("Net", "network became available — rescanning and fetching relay");
                Coordinator.Discovery.SendBeacon();
                _ = FetchRelayMessagesAsync("network-up");
            }
        };
        // Unicast beacon hints: last-known IPs of saved contacts that aren't
        // currently online. Reaches contacts across subnets or on networks that
        // filter broadcast/multicast — the main reason saved contacts were slow
        // to (re)discover. Runs on the beacon timer thread; a rare concurrent
        // contact-list mutation is absorbed by DiscoveryService's guard.
        Coordinator.UnicastHints = () =>
        {
            var online = Peers.Values.Where(p => p.IsOnline).Select(p => p.IP).ToHashSet();
            return ConfigStore.Shared.Config.Contacts
                .Select(c => c.LastIP)
                .Where(ip => ip.Length > 0 && !online.Contains(ip))
                .Distinct()
                .Take(32)
                .ToList();
        };

        NotificationService.Shared.Register();
        MigrateConversationLists();
        LoadHistory();
        StartPeerTimeoutTimer();
        CheckMigration();
        ScheduleAutoUpdateCheck();
        StartRelayPolling();

        // Announce departure on sleep so peers flip us offline instantly instead
        // of waiting out the silence timeout; re-announce on wake. The handler
        // runs on a SystemEvents thread — SendGoodbye is thread-safe, but UI
        // state must hop to the dispatcher.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case Microsoft.Win32.PowerModes.Suspend:
                Coordinator.SendGoodbye();
                _dq.TryEnqueue(() => MarkAllPeersOffline("sleep"));
                break;
            case Microsoft.Win32.PowerModes.Resume:
                _dq.TryEnqueue(() =>
                {
                    // Sleep frequently invalidates UDP sockets without any
                    // network-change event — rebuild before announcing.
                    Coordinator.Discovery.RebuildNow("resume");
                    Coordinator.Discovery.SendBeacon();
                });
                break;
        }
    }

    // MARK: - Migration

    /// <summary>
    /// Re-files the archived and hidden lists from addresses to identity keys,
    /// the same way HistoryStore re-files history at load. Idempotent: a key is
    /// already an id, so once done this changes nothing.
    /// </summary>
    private static void MigrateConversationLists()
    {
        var config = ConfigStore.Shared.Config;
        var contacts = config.Contacts.Select(c => new PeerId.Contact(c.PublicKeyB64, c.LastIP)).ToList();
        var archived = PeerId.Rekey(config.ArchivedConversations, contacts);
        var hidden   = PeerId.Rekey(config.HiddenConversations, contacts);
        if (archived.SequenceEqual(config.ArchivedConversations) && hidden.SequenceEqual(config.HiddenConversations))
            return;
        config.ArchivedConversations = archived;
        config.HiddenConversations = hidden;
        ConfigStore.Shared.Save();
        LanLogger.Info("History", "re-filed archived/hidden conversation lists by identity key");
    }

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

    private void UpsertPeer(string ip, string username, int port, string publicKeyB64,
                            string? relayIdHash = null, List<string>? advertisedIPs = null,
                            List<string>? caps = null)
    {
        // Last-resort self-suppression — defends against stale OwnIPs in the
        // discovery service when the machine's network interfaces change.
        if (string.IsNullOrEmpty(publicKeyB64) ||
            publicKeyB64 == KeyManager.Shared.PublicKeyB64) return;
        if (Coordinator.Network.LocalIPs.Contains(ip)) return;

        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == publicKeyB64);
        if (contact is not null)
        {
            // Refresh the stored display name when the peer broadcasts a real
            // name and the local copy is the bare default. We don't overwrite
            // names the user explicitly set themselves.
            var cleaned = (username ?? "").Trim();
            if (cleaned.Length > 0 && cleaned != "User"
                && contact.Username != cleaned
                && (string.IsNullOrEmpty(contact.Username) || contact.Username is "User" or "Unknown"))
            {
                contact.Username = cleaned;
                ConfigStore.Shared.Save();
            }
        }
        if (contact is not null && contact.LastIP != ip)
        {
            // Only a hint now: where to send unicast beacons, and what the trust
            // check compares against. The conversation is filed by key, so a new
            // address moves nothing.
            contact.LastIP = ip;
            ConfigStore.Shared.Save();
        }

        // Every address the peer has advertised, used as probe targets. The
        // current source IP goes first.
        var knownIPs = advertisedIPs is null ? new List<string>() : new List<string>(advertisedIPs);
        knownIPs.Remove(ip);
        knownIPs.Insert(0, ip);

        var current = Peers;
        var updated = new Dictionary<string, PeerInfo>(current);

        // An address belongs to one device at a time. Whoever held it before is
        // no longer reachable there, and leaving them marked online at it is how
        // something meant for them reaches this peer instead.
        foreach (var other in updated.Values.Where(p =>
                     p.PublicKeyB64 != publicKeyB64 && p.IP == ip && p.IsOnline).ToList())
        {
            LanLogger.Peer(event_: "presence", peer: ip, publicKey: other.PublicKeyB64,
                           reason: $"Online -> Offline address now held by {publicKeyB64[..Math.Min(8, publicKeyB64.Length)]}");
            other.Presence = PeerPresence.Offline;
            other.LastSeen = DateTime.MinValue;
        }

        if (updated.TryGetValue(publicKeyB64, out var existing))
        {
            if (existing.IP == ip)
            {
                // Same IP — a heartbeat: presence is online and the silence clock
                // resets. Check whether the peer was offline before this beacon so
                // we can refresh the UI on the comeback.
                var wasOffline = !existing.IsOnline;
                existing.LastSeen = DateTime.UtcNow;
                existing.Presence = PeerPresence.Online;
                existing.KnownIPs = knownIPs;
                if (wasOffline)
                {
                    LanLogger.Info("Net", $"peer {ip} ({publicKeyB64[..8]}) came back online");
                    // Reassign Peers so the chat header refreshes on the comeback.
                    Peers = updated;
                    AdoptRelayPlaceholder(publicKeyB64);
                    RefreshConversations();
                }
                // Drain queues on EVERY heartbeat, not just the offline→online
                // transition (matches macOS). A message that failed on a
                // transient TCP error while the peer stayed online used to sit
                // "Queued" until the peer bounced; now the next beacon retries
                // it. Both calls self-throttle and are no-ops with empty queues.
                MessagingService.Shared.DeliverPending(ip, publicKeyB64);
                DeliverPendingFiles(publicKeyB64, ip);
                return;
            }
            // IP changed — fall through to replace the entry below.
        }
        updated[publicKeyB64] = new PeerInfo
        {
            IP = ip, Username = username, Port = port,
            PublicKeyB64 = publicKeyB64, LastSeen = DateTime.UtcNow,
            Presence = PeerPresence.Online, KnownIPs = knownIPs,
            Caps = caps ?? [],
        };
        Peers = updated;
        if (!string.IsNullOrEmpty(relayIdHash))
        {
            _peerRelayIdHashes[publicKeyB64] = relayIdHash;
            // Persist relay hash into the contact so it survives app restarts.
            // Without this, messages to offline peers skip the relay because
            // _peerRelayIdHashes is only populated from live discovery packets.
            var c = ConfigStore.Shared.Config.Contacts.FirstOrDefault(x => x.PublicKeyB64 == publicKeyB64);
            if (c is not null && c.RelayIdHash != relayIdHash)
            {
                c.RelayIdHash = relayIdHash;
                ConfigStore.Shared.Save();
            }
        }

        AdoptRelayPlaceholder(publicKeyB64);
        RefreshConversations();
        MessagingService.Shared.DeliverPending(ip, publicKeyB64);
        DeliverPendingFiles(publicKeyB64, ip);
    }

    /// <summary>
    /// A message decrypted under <paramref name="key"/> arrived from
    /// <paramref name="ip"/>, so that device is there now. Covers a peer whose
    /// beacons never reach us: without it, somebody who messages us could not
    /// be answered until discovery found them. A peer already online elsewhere
    /// keeps its discovered address — discovery is the authority on where a live
    /// peer is, and a message is only evidence where discovery has none.
    /// </summary>
    private void NotePeerAddress(string key, string ip, string sender)
    {
        if (!PeerId.IsKey(key) || string.IsNullOrEmpty(ip) || key == KeyManager.Shared.PublicKeyB64
            || Coordinator.Network.LocalIPs.Contains(ip)) return;
        var updated = new Dictionary<string, PeerInfo>(Peers);
        if (updated.TryGetValue(key, out var info))
        {
            var moved = !info.IsOnline && info.IP != ip;
            var wasOffline = !info.IsOnline;
            if (moved)
            {
                LanLogger.Peer(event_: "address", peer: ip, publicKey: key,
                               reason: $"learned from an authenticated message (was {info.IP})");
                var knownIPs = new List<string>(info.KnownIPs);
                knownIPs.Remove(ip);
                knownIPs.Insert(0, ip);
                // PeerInfo.IP is init-only: a moved peer is a new record.
                info = updated[key] = new PeerInfo
                {
                    IP = ip, Username = info.Username, Port = info.Port,
                    PublicKeyB64 = key, KnownIPs = knownIPs, Caps = info.Caps,
                };
            }
            info.LastSeen = DateTime.UtcNow;
            info.Presence = PeerPresence.Online;
            if (!wasOffline) return;
            Peers = updated;
            RefreshConversations();
            if (moved)
            {
                MessagingService.Shared.DeliverPending(ip, key);
                DeliverPendingFiles(key, ip);
            }
            return;
        }
        LanLogger.Peer(event_: "address", peer: ip, publicKey: key,
                       reason: "learned from an authenticated message (never discovered)");
        // Capabilities stay empty until a beacon says otherwise: absence must
        // never be read as support.
        updated[key] = new PeerInfo
        {
            IP = ip, Username = sender, Port = 54232, PublicKeyB64 = key,
            LastSeen = DateTime.UtcNow, Presence = PeerPresence.Online, KnownIPs = [ip],
        };
        Peers = updated;
        RefreshConversations();
    }

    /// <summary>
    /// Where the device with identity key <paramref name="key"/> can be reached
    /// right now, or "" when it is not on the LAN. The only way an address is
    /// chosen for anything sent: never a remembered one, which may have been
    /// handed to somebody else since.
    /// </summary>
    public string LiveAddress(string key) =>
        Peers.TryGetValue(key, out var p) && p.IsOnline ? p.IP : "";

    /// <summary>
    /// Whether <paramref name="ip"/> is an address the device holding
    /// <paramref name="key"/> is known at. The gate for packets that only claim a
    /// sender (see MessagingService.IsBoundAddress). A live peer is known at the
    /// addresses discovery has seen it use; one not seen this session, at the
    /// address its contact entry last recorded.
    /// </summary>
    private bool IsBoundAddress(string key, string ip)
    {
        if (Peers.TryGetValue(key, out var p)) return p.IP == ip || p.KnownIPs.Contains(ip);
        return ConfigStore.Shared.Config.Contacts.Any(c => c.PublicKeyB64 == key && c.LastIP == ip);
    }

    /// <summary>
    /// Re-files history an older build put under <c>relay-&lt;key prefix&gt;</c> —
    /// relay messages from a peer it had never met on the LAN — once that peer is
    /// known. Migration at load already did this for contacts; this catches
    /// anybody else.
    /// </summary>
    private void AdoptRelayPlaceholder(string key)
    {
        var placeholder = PeerId.RelayPlaceholder(key);
        if (!HistoryStore.Shared.Merge(placeholder, key)) return;
        HistoryStore.Shared.Save();
        var msgs = new Dictionary<string, List<MessageEntry>>(Messages);
        msgs.Remove(placeholder);
        msgs[key] = new List<MessageEntry>(HistoryStore.Shared.Entries(key));
        Messages = msgs;
        ConfigStore.Shared.Config.HiddenConversations.RemoveAll(x => x == placeholder);
        ConfigStore.Shared.Config.ArchivedConversations.RemoveAll(x => x == placeholder);
        ConfigStore.Shared.Save();
        if (SelectedConversationId == placeholder) SelectedConversationId = key;
        LanLogger.Info("Relay", $"re-filed relay history from {placeholder} to {key[..Math.Min(8, key.Length)]}");
    }

    private void TouchPeer(string? publicKeyB64)
    {
        if (string.IsNullOrEmpty(publicKeyB64)) return;
        var current = Peers;
        if (!current.TryGetValue(publicKeyB64, out var existing)) return;
        var wasOffline = existing.Presence == PeerPresence.Offline;
        existing.LastSeen = DateTime.UtcNow;
        existing.Presence = PeerPresence.Online;
        // Any inbound TCP traffic proves the peer is back — surface it at once
        // rather than waiting for the next discovery beacon. Reassign Peers so the
        // chat header (which listens for the Peers change) refreshes too.
        if (wasOffline)
        {
            Peers = new Dictionary<string, PeerInfo>(current);
            RefreshConversations();
        }
    }

    // A completed outbound TCP send (message or file) to a peer's live address
    // is proof that peer is still reachable — at least as strong as a discovery
    // beacon — so a peer we are talking to stays online between its own
    // beacons. Keyed: it refreshes the device the send was addressed to, and
    // only while that device is still recorded at the address that answered.
    private void MarkPeerReachable(string key, string ip)
    {
        if (Peers.TryGetValue(key, out var p) && p.IP == ip) TouchPeer(key);
    }

    // How long a non-contact peer may sit offline before it is dropped from the
    // dict. Contacts are kept indefinitely (their key is needed to queue/relay).
    private static readonly TimeSpan NonContactPruneAfter = TimeSpan.FromSeconds(300);

    // Drives the LAN presence state machine. Runs every second: re-evaluates
    // every peer from its LastSeen, actively probes the ones that have gone
    // quiet, flips presence on transitions, and prunes long-gone non-contacts.
    // Peers are NOT deleted the instant they go offline — presence is explicit,
    // so an offline contact stays in the dict and the row simply shows gray.
    private void StartPeerTimeoutTimer()
    {
        _peerTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _peerTimeoutTimer.Tick += (_, _) => EvaluatePresence();
        _peerTimeoutTimer.Start();
    }

    private void EvaluatePresence()
    {
        var now = DateTime.UtcNow;
        if (Peers.Count == 0) return;
        var contactKeys = ConfigStore.Shared.Config.Contacts.Select(c => c.PublicKeyB64).ToHashSet();
        HashSet<string>? probeTargets = null;
        List<string>? pruneKeys = null;
        var changed = false;

        foreach (var (key, info) in Peers)
        {
            var decision = PresenceEvaluator.Decide(info.LastSeen, now);

            // Prune non-contact peers gone a long time so the dict can't grow
            // without bound from one-off discovered devices.
            if (decision == PresenceEvaluator.Decision.Offline
                && !contactKeys.Contains(key)
                && now - info.LastSeen > NonContactPruneAfter)
            {
                (pruneKeys ??= []).Add(key);
                changed = true;
                continue;
            }

            if (decision.ShouldProbe())
            {
                // Reconfirm via unicast before declaring offline. Probe every
                // address the peer has advertised, not just the last one.
                probeTargets ??= [];
                if (info.KnownIPs.Count == 0) probeTargets.Add(info.IP);
                else foreach (var ip in info.KnownIPs) probeTargets.Add(ip);
            }

            var presence = decision.Presence();
            if (info.Presence != presence)
            {
                // Every presence flip gets a line. Presence drives queueing and
                // relay routing, so "why did my message queue?" is answerable
                // only if the transition that caused it is on the record.
                LanLogger.Peer(
                    event_: "presence",
                    peer:      info.IP,
                    publicKey: key,
                    reason:    $"{info.Presence} -> {presence} quiet_ms=" +
                               $"{(long)(now - info.LastSeen).TotalMilliseconds}");
                info.Presence = presence;
                changed = true;
            }
        }

        if (probeTargets is not null)
            foreach (var ip in probeTargets) Coordinator.Probe(ip);

        // Only copy the dictionary (and wake every Peers observer) when
        // something actually changed — this ticks every second.
        if (changed)
        {
            var updated = new Dictionary<string, PeerInfo>(Peers);
            if (pruneKeys is not null)
                foreach (var key in pruneKeys) updated.Remove(key);
            Peers = updated;
            RefreshConversations();
        }
    }

    // A peer announced its departure (clean quit / sleep / network loss). Flip it
    // offline immediately and push LastSeen into the past so the next presence
    // tick agrees and won't bounce it back online.
    private void HandleGoodbye(string publicKeyB64, string fromIP)
    {
        if (string.IsNullOrEmpty(publicKeyB64)) return;
        var current = Peers;
        if (!current.TryGetValue(publicKeyB64, out var existing)) return;
        LanLogger.Info("Net", $"peer {publicKeyB64[..Math.Min(8, publicKeyB64.Length)]} said goodbye — marking offline");
        existing.Presence = PeerPresence.Offline;
        existing.LastSeen = DateTime.MinValue;
        Peers = new Dictionary<string, PeerInfo>(current);
        RefreshConversations();
    }

    // Flip every known peer offline locally (we've lost the ability to observe
    // them). LastSeen is aged out so the presence tick stays in agreement.
    private void MarkAllPeersOffline(string reason)
    {
        if (Peers.Count == 0) return;
        LanLogger.Info("Net", $"marking all peers offline ({reason})");
        foreach (var info in Peers.Values)
        {
            info.Presence = PeerPresence.Offline;
            info.LastSeen = DateTime.MinValue;
        }
        Peers = new Dictionary<string, PeerInfo>(Peers);
        RefreshConversations();
    }

    // MARK: - Conversations

    // Public entry point used everywhere — coalesces bursts of calls to a
    // single rebuild per dispatcher tick. Callers that need the rebuild to
    // complete synchronously (initial load, contact mutations) can call
    // RefreshConversationsNow directly.
    private void RefreshConversations()
    {
        if (_refreshConvosScheduled) return;
        _refreshConvosScheduled = true;
        if (!_dq.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _refreshConvosScheduled = false;
            RefreshConversationsNow();
        }))
        {
            // Dispatcher not available — fall back to synchronous refresh.
            _refreshConvosScheduled = false;
            RefreshConversationsNow();
        }
    }

    private void RefreshConversationsNow()
    {
        // Threads only exist for saved contacts (or peers we have history with) —
        // random discovered peers must not auto-appear as conversations.
        // `HiddenConversations` covers threads the user deleted; the contact
        // stays saved so the user can re-open the thread from "New message".
        var hidden   = ConfigStore.Shared.Config.HiddenConversations.ToHashSet();
        var archived = ConfigStore.Shared.Config.ArchivedConversations.ToHashSet();
        var active   = new List<ConversationViewModel>();
        var arch     = new List<ConversationViewModel>();
        var seen     = new HashSet<string>();

        // Saved contacts — include whether currently online or offline. One row
        // per identity key, wherever that device happens to be. Deduplicating by
        // address instead meant two contacts DHCP had put on one address shared
        // a row, and one of the two threads vanished from the list.
        foreach (var contact in ConfigStore.Shared.Config.Contacts)
        {
            var key = contact.PublicKeyB64;
            if (hidden.Contains(key) || !seen.Add(key)) continue;
            var entries = Messages.TryGetValue(key, out var list) ? list : [];
            var last    = entries.Count > 0 ? entries[^1] : null;
            var typing  = TypingStates.TryGetValue(key, out var t) ? t : default;
            var vm = new ConversationViewModel
            {
                ConversationId   = key,
                PeerName         = contact.Username,
                PeerPublicKeyB64 = key,
                PhotoB64         = contact.PhotoB64,
                LastMessage      = LastMessagePreview(entries),
                LastTimestamp    = last is not null
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(last.Timestamp * 1000)).UtcDateTime
                    : null,
                UnreadCount  = CountUnread(entries),
                IsTyping     = typing.Active,
                TypingSender = typing.Sender ?? "",
                IsOnline     = Peers.TryGetValue(key, out var p) && p.IsOnline,
                IsArchived   = archived.Contains(key),
            };
            (vm.IsArchived ? arch : active).Add(vm);
        }

        // Anybody we have history with but no contact entry — e.g. someone who
        // messaged us once and isn't saved — plus legacy threads no contact
        // could be matched to. Don't lose those.
        foreach (var (id, entries) in Messages)
        {
            if (hidden.Contains(id) || seen.Contains(id) || entries.Count == 0) continue;
            var last = entries[^1];
            var key  = PeerId.IsKey(id) ? id : "";
            var peer = key.Length > 0 ? Peers.GetValueOrDefault(key) : null;
            var name = entries.LastOrDefault(e => e.Incoming)?.Sender
                       ?? peer?.Username
                       ?? PeerId.LegacyAddress(id)
                       ?? "Unknown";
            var typing = TypingStates.TryGetValue(id, out var t) ? t : default;
            var vm = new ConversationViewModel
            {
                ConversationId   = id,
                PeerName         = name,
                PeerPublicKeyB64 = key,
                LastMessage      = LastMessagePreview(entries),
                LastTimestamp    = DateTimeOffset.FromUnixTimeMilliseconds((long)(last.Timestamp * 1000)).UtcDateTime,
                UnreadCount      = CountUnread(entries),
                IsTyping         = typing.Active,
                TypingSender     = typing.Sender ?? "",
                IsOnline         = peer?.IsOnline ?? false,
                IsArchived       = archived.Contains(id),
            };
            (vm.IsArchived ? arch : active).Add(vm);
        }

        active.Sort((a, b) =>
            (b.LastTimestamp ?? DateTime.MinValue).CompareTo(a.LastTimestamp ?? DateTime.MinValue));
        arch.Sort((a, b) =>
            (b.LastTimestamp ?? DateTime.MinValue).CompareTo(a.LastTimestamp ?? DateTime.MinValue));
        Conversations = active;
        ArchivedConversations = arch;
        TotalUnreadCount = active.Sum(c => c.UnreadCount);
    }

    private static int CountUnread(IReadOnlyList<MessageEntry> entries)
        => entries.Count(e => e.Incoming && !e.ReadReceiptSent);

    public static string LastMessagePreview(IReadOnlyList<MessageEntry> entries)
    {
        if (entries.Count == 0) return "";
        var last = entries[^1];
        if (last.Deleted) return "This message was deleted";
        if (last.Text.StartsWith("__FILE__:"))
        {
            var path = last.Text["__FILE__:".Length..];
            return "📎 " + Path.GetFileName(path);
        }
        // An audit record is not a message. Without this the sidebar shows the
        // raw JSON body, which is how a privacy feature ends up looking broken.
        if (RemoteAuditRecord.IsAudit(last.Text)) return RemoteAuditRecord.SummaryOf(last.Text);
        return CollapseWhitespace(last.Text);
    }

    // Collapses newlines and runs of whitespace so a multi-line message renders
    // as a short single-paragraph preview in the sidebar instead of stretching
    // the row to match the message's original line count.
    private static string CollapseWhitespace(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // MARK: - Messaging

    /// <summary>
    /// Sends to the peer with identity key <paramref name="peer"/>. The address is
    /// looked up now, not remembered: a peer that is not on the LAN gets its
    /// message queued and relayed rather than dialled at an address that may
    /// belong to somebody else by now. That used to be the rule's opposite — the
    /// key was looked up from the thread's address, so a thread whose address had
    /// been handed to another device could encrypt to that device's key.
    /// </summary>
    public void SendMessage(string text, string peer, MessageEntry? replyTo = null)
    {
        if (!PeerId.IsKey(peer))
        {
            LanLogger.Warn("Send", $"conversation {peer} has no identity key — not sending");
            return;
        }
        var address = LiveAddress(peer);
        // Relay is ONLY used when the peer is confirmed offline. If the peer is
        // currently online and TCP fails, that is a transient error — queue locally
        // but do not upload to the cloud relay to avoid spurious relay deliveries.
        var peerIsOnline = address.Length > 0;
        var relayIdHash = peerIsOnline ? null : RelayIdHashForPeerKey(peer);
        LanLogger.Info("Send", $"routing for peer={peer[..8]} online={peerIsOnline} relay={relayIdHash is not null}");
        MessagingService.Shared.SendText(text, address, peer, relayIdHash, replyTo);
    }

    public void SendTyping(bool active, string peer)
    {
        var address = LiveAddress(peer);
        if (address.Length == 0) return;
        MessagingService.Shared.SendTyping(active, address, peer);
    }

    public void SendReadReceipt(MessageEntry entry, string peer)
    {
        if (!entry.Incoming || entry.MessageId is null || entry.ReadReceiptSent) return;
        MarkConversationRead(peer);
    }

    // Marks every incoming unread message for the given conversation as read,
    // sends read receipts, and updates the in-memory `Messages` so the unread
    // badge clears. A legacy thread has nobody to tell; its messages are still read.
    public void MarkConversationRead(string peer)
    {
        if (!Messages.TryGetValue(peer, out var list) || list.Count == 0) return;
        var address = PeerId.IsKey(peer) ? LiveAddress(peer) : "";
        var anyChanged = false;
        foreach (var e in list)
        {
            if (!e.Incoming || e.ReadReceiptSent) continue;
            // Send read_receipt for any entry that has a stable ID (text messages
            // and file entries that carry a transfer_id as their MessageId).
            if (e.MessageId is { } id && address.Length > 0)
                MessagingService.Shared.SendReceipt("read_receipt", id, address);
            e.ReadReceiptSent = true;
            anyChanged = true;
        }
        if (anyChanged)
        {
            // Persist readReceiptSent for all entry types, including file entries
            // that have no MessageId — MarkReadReceiptSent alone misses those.
            HistoryStore.Shared.MarkAllIncomingRead(peer);
            HistoryStore.Shared.Save();
            OnPropertyChanged(nameof(Messages));
            RefreshConversations();
        }
    }

    // Deletes a single message. "Delete for everyone" marks the entry as deleted
    // (clearing text/reply fields) both locally and on the peer's copy via
    // delete_message; only the sender's own outgoing messages qualify. "Delete
    // for me" removes the entry from the local history only — no packet is sent.
    public void DeleteMessage(MessageEntry entry, string peer, bool forEveryone)
    {
        if (forEveryone)
        {
            if (entry.Incoming || string.IsNullOrEmpty(entry.MessageId)) return;
            // A legacy thread has no key to tell anybody with.
            if (!PeerId.IsKey(peer)) return;
            // An audit record has no copy on the peer to delete; it exists only
            // in this machine's history.
            if (RemoteAuditRecord.IsAudit(entry.Text)) return;
            HistoryStore.Shared.MarkDeleted(entry.MessageId, peer, requireIncoming: false);
            HistoryStore.Shared.Save();
            if (Messages.TryGetValue(peer, out var list))
            {
                var e = list.FirstOrDefault(x => x.MessageId == entry.MessageId);
                if (e is not null)
                {
                    e.Deleted          = true;
                    e.Text             = "";
                    e.ReplyToMessageId = null;
                    e.ReplyToPreview   = null;
                    e.ReplyToSender    = null;
                }
            }
            // An empty address fails the LAN write at once and goes straight to
            // the relay control record.
            MessagingService.Shared.SendDeleteMessage(
                entry.MessageId, LiveAddress(peer), peer, RelayIdHashForPeerKey(peer));
            OnPropertyChanged(nameof(Messages));
            RefreshConversations();
        }
        else
        {
            HistoryStore.Shared.RemoveEntry(entry, peer);
            HistoryStore.Shared.Save();
            if (Messages.TryGetValue(peer, out var list))
            {
                var idx = list.FindIndex(e => MessageEntry.SameEntry(e, entry));
                if (idx >= 0) list.RemoveAt(idx);
            }
            OnPropertyChanged(nameof(Messages));
            RefreshConversations();
        }
    }

    /// <summary>Whether an entry may be edited from this machine.</summary>
    /// <remarks>
    /// Only our own outgoing text messages qualify: an attachment's Text is a
    /// local file path, not a body; a deleted message has no body left; and an
    /// audit record has no body to replace, since rewriting the trail is the one
    /// thing it exists to prevent. The chat row's Edit menu item asks this same
    /// question, so the menu never offers an edit that EditMessage refuses.
    /// </remarks>
    public static bool IsEditable(MessageEntry entry) =>
        !entry.Incoming
        && !string.IsNullOrEmpty(entry.MessageId)
        && !entry.Deleted
        && !entry.Text.StartsWith("__FILE__:", StringComparison.Ordinal)
        && !RemoteAuditRecord.IsAudit(entry.Text);

    /// <summary>
    /// Replaces the text of one of our own outgoing messages, locally and on
    /// the peer. Returns false when the message isn't editable (see
    /// IsEditable), so the caller can leave the composer in edit mode rather
    /// than silently dropping it.
    /// </summary>
    public bool EditMessage(MessageEntry entry, string newText, string peer)
    {
        if (!IsEditable(entry)) return false;
        // Nobody to send the replacement to in a legacy thread.
        if (!PeerId.IsKey(peer)) return false;
        string messageId = entry.MessageId!;   // IsEditable requires one

        var trimmed = newText.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        // A no-op edit still costs a packet and adds an "(edited)" marker the
        // user didn't ask for.
        if (trimmed == entry.Text) return true;

        var editedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (!HistoryStore.Shared.ApplyEdit(messageId, peer, trimmed, editedAt, requireIncoming: false))
            return false;
        HistoryStore.Shared.Save();

        if (Messages.TryGetValue(peer, out var list))
        {
            var e = list.FirstOrDefault(x => x.MessageId == messageId);
            if (e is not null)
            {
                e.Text     = trimmed;
                e.Edited   = true;
                e.EditedAt = editedAt;
            }
        }

        // An offline peer still gets it: the queued-message rewrite inside
        // SendEditMessage, and the relay control record when the LAN write
        // fails — which an empty address does at once.
        MessagingService.Shared.SendEditMessage(messageId, trimmed, LiveAddress(peer), peer, editedAt,
                                                RelayIdHashForPeerKey(peer));

        OnPropertyChanged(nameof(Messages));
        RefreshConversations();
        return true;
    }

    /// <summary>
    /// The peer's relay mailbox address, from the live session cache or the
    /// saved contact.
    ///
    /// Unlike SendMessage, this is not gated on the peer being offline. A new
    /// message that fails a TCP write stays in the pending queue and retries;
    /// an edit or delete has no queue, so it is simply lost if the one write
    /// fails. Both operations are idempotent, so a relay copy that turns out to
    /// be redundant costs nothing.
    /// </summary>
    private string? RelayIdHashForPeerKey(string key) =>
        _peerRelayIdHashes.GetValueOrDefault(key)
        ?? ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == key)?.RelayIdHash;

    // Queue or send a file. If the peer is offline, the path is persisted in
    // config and retried whenever the peer comes back online.
    public bool SendFile(string filePath, string peer)
    {
        if (!File.Exists(filePath))
        {
            LanLogger.Warn("Attachment", $"Cannot send missing file: {filePath}");
            return false;
        }
        // The conversation id is the key — there is no longer a window, right
        // after startup, in which a thread is known but its key is not. A legacy
        // thread has none and cannot be sent to.
        if (!PeerId.IsKey(peer))
        {
            LanLogger.Warn("Attachment", $"conversation {peer} has no identity key — not sending \"{Path.GetFileName(filePath)}\"");
            return false;
        }
        var publicKey = peer;

        // Stream immediately only when the peer is actually online. Offline peers
        // stay in the dict (presence is explicit), so test IsOnline rather than
        // mere existence — otherwise the file would skip the persisted queue.
        var address = LiveAddress(peer);
        if (address.Length > 0)
        {
            FileTransferService.Shared.Enqueue(filePath, peer, address);
            return true;
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
        HistoryStore.Shared.Append(entry, peer);
        HistoryStore.Shared.Save();
        var msgs = new Dictionary<string, List<MessageEntry>>(Messages);
        if (!msgs.TryGetValue(peer, out var l)) l = msgs[peer] = [];
        l.Add(entry);
        Messages = msgs;
        RefreshConversations();
        return true;
    }

    private void DeliverPendingFiles(string peerPublicKeyB64, string peerIP)
    {
        // 1) Re-trigger any in-memory queue that stalled on an earlier failed attempt.
        FileTransferService.Shared.RetryQueue(peerPublicKeyB64, peerIP);

        // 2) Drain the persistent pending-file queue for this peer.
        var matching = ConfigStore.Shared.Config.PendingFiles
            .Where(f => f.PeerPublicKeyB64 == peerPublicKeyB64).ToList();
        if (matching.Count == 0) return;

        foreach (var item in matching)
        {
            if (!File.Exists(item.FilePath)) continue;
            FileTransferService.Shared.Enqueue(item.FilePath, peerPublicKeyB64, peerIP);
        }

        ConfigStore.Shared.Config.PendingFiles.RemoveAll(f => f.PeerPublicKeyB64 == peerPublicKeyB64);
        ConfigStore.Shared.Save();
    }

    // MARK: - Cloud relay

    /// Starts both an immediate relay fetch and a recurring poll. The poll keeps
    /// the inbox drained while the app is foregrounded so messages stored on the
    /// Worker while a peer was unreachable deliver promptly without a restart.
    private void StartRelayPolling()
    {
        _ = FetchRelayMessagesAsync("startup");
        RetryRelayOutbox();
        _relayPollTimer = new DispatcherTimer { Interval = RelayPollInterval };
        _relayPollTimer.Tick += (_, _) =>
        {
            _ = FetchRelayMessagesAsync("poll");
            RetryRelayOutbox();
        };
        _relayPollTimer.Start();
    }

    /// Retries the cloud-relay upload for any locally-queued message whose
    /// store was never confirmed by the Worker (transient failure, cold
    /// start, a momentarily full inbox, etc). Runs on the same cadence as
    /// the inbox poll so a message that failed to upload isn't stuck relying
    /// solely on both peers later being on the LAN simultaneously.
    private void RetryRelayOutbox()
    {
        var unconfirmed = ConfigStore.Shared.Config.PendingMessages.Where(m => !m.RelayStored).ToList();
        foreach (var msg in unconfirmed)
        {
            // Same relay-hash resolution as SendMessage: prefer the live
            // session cache, fall back to the contact's persisted hash.
            _peerRelayIdHashes.TryGetValue(msg.PeerPublicKeyB64, out var hash);
            hash ??= ConfigStore.Shared.Config.Contacts
                .FirstOrDefault(c => c.PublicKeyB64 == msg.PeerPublicKeyB64)?.RelayIdHash;
            if (string.IsNullOrEmpty(hash)) continue;
            MessagingService.Shared.RetryRelayStore(msg.MessageId, hash);
        }
    }

    /// Fetches messages waiting in the cloud relay Worker mailbox and dispatches
    /// them through MessagingService. Silent no-op when the relay URL is empty.
    /// Logs reason + outcome so the relay flow is auditable from client.log.
    private async Task FetchRelayMessagesAsync(string reason)
    {
        if (_relayFetchInFlight)
        {
            LanLogger.Info("Relay", $"fetch skipped ({reason}) — previous request still in flight");
            return;
        }
        _relayFetchInFlight = true;
        try
        {
            LanLogger.Info("Relay", $"fetch start reason={reason}");
            var msgs = await RelayClient.Shared.FetchPendingAsync();
            if (msgs.Count == 0)
            {
                LanLogger.Info("Relay", $"fetch done reason={reason} — no pending messages");
                return;
            }
            LanLogger.Info("Relay", $"fetch done reason={reason} — delivering {msgs.Count} message(s)");
            _dq.TryEnqueue(() =>
            {
                foreach (var msg in msgs)
                {
                    // Filed under the sender's key. The address is only where a
                    // delivery receipt can go, and a sender not on the LAN right
                    // now simply doesn't get one.
                    MessagingService.Shared.HandleRelayMessage(msg, LiveAddress(msg.SenderPublicKeyB64));
                }
                RefreshConversations();
            });
        }
        finally
        {
            _relayFetchInFlight = false;
        }
    }

    // MARK: - Conversation / contact actions

    public void ArchiveConversation(string peer)
    {
        if (!ConfigStore.Shared.Config.ArchivedConversations.Contains(peer))
        {
            ConfigStore.Shared.Config.ArchivedConversations.Add(peer);
            ConfigStore.Shared.Save();
        }
        if (SelectedConversationId == peer) SelectedConversationId = null;
        RefreshConversations();
    }

    public void UnarchiveConversation(string peer)
    {
        ConfigStore.Shared.Config.ArchivedConversations.RemoveAll(x => x == peer);
        ConfigStore.Shared.Save();
        RefreshConversations();
    }

    // Deletes a conversation: removes message history and hides the thread from the
    // sidebar. The contact stays in the saved contacts list — re-open the thread
    // through the "New message" picker.
    public void DeleteConversation(string peer)
    {
        var msgs = new Dictionary<string, List<MessageEntry>>(Messages);
        msgs.Remove(peer);
        Messages = msgs;
        Drafts.Remove(peer);
        HistoryStore.Shared.Delete(peer);
        HistoryStore.Shared.Save();
        if (!ConfigStore.Shared.Config.HiddenConversations.Contains(peer))
            ConfigStore.Shared.Config.HiddenConversations.Add(peer);
        ConfigStore.Shared.Config.ArchivedConversations.RemoveAll(x => x == peer);
        ConfigStore.Shared.Save();
        if (SelectedConversationId == peer) SelectedConversationId = null;
        RefreshConversations();
    }

    // Unhide a contact's thread and select it so the user can chat with them.
    public void StartConversation(string publicKeyB64)
    {
        if (!ConfigStore.Shared.Config.Contacts.Any(c => c.PublicKeyB64 == publicKeyB64)) return;
        ConfigStore.Shared.Config.HiddenConversations.RemoveAll(h => h == publicKeyB64);
        ConfigStore.Shared.Save();
        RefreshConversations();
        SelectedConversationId = publicKeyB64;
    }

    public void DeleteContact(string publicKeyB64)
    {
        if (ConfigStore.Shared.Config.Contacts.RemoveAll(c => c.PublicKeyB64 == publicKeyB64) == 0) return;
        ConfigStore.Shared.Save();
        DeleteConversation(publicKeyB64);
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

        MessagingService.Shared.OnPeerReachable    = MarkPeerReachable;
        FileTransferService.Shared.OnPeerReachable = MarkPeerReachable;
        MessagingService.Shared.IsBoundAddress     = IsBoundAddress;
        FileTransferService.Shared.IsBoundAddress  = IsBoundAddress;
        MessagingService.Shared.OnPeerAddressProven = NotePeerAddress;

        Coordinator.PacketReceived += pkt =>
        {
            // Refresh LastSeen for the sender so TCP activity (text, typing,
            // receipts, file chunks) keeps them marked online — mirrors macOS touchPeer.
            // Gated rather than unconditional: media_attach arrives on a socket
            // that is about to stop being a JSON peer connection at all. And only
            // from an address that key is known at: the key in most packets is a
            // claim, and a claim from anywhere would let any host mark any peer
            // reachable at an address it has left. A peer that has moved is
            // caught by discovery, or by NotePeerAddress once a message it sent
            // from the new address decrypts.
            if (pkt.RefreshesPresence && pkt.SenderPublicKeyB64 is { Length: > 0 } sender
                && IsBoundAddress(sender, pkt.SenderIP))
                TouchPeer(sender);
            switch (pkt)
            {
                case ValidatedText or ValidatedTyping or ValidatedReceipt or ValidatedDelete:
                    MessagingService.Shared.HandlePacket(pkt); break;
                case ValidatedFileStart or ValidatedFileChunk or ValidatedFileEnd:
                    FileTransferService.Shared.HandlePacket(pkt); break;
                case ValidatedRemoteInvite or ValidatedRemoteAccept
                     or ValidatedRemoteDecline or ValidatedRemoteEnd:
                    Core.Services.RemoteDesktopService.Shared.HandleControlPacket(pkt);
                    break;
                case ValidatedMediaAttach:
                    // Handled synchronously inside NetworkCoordinator.HandleInbound,
                    // on the connection's own task, because the socket has to be
                    // detached before the JSON read loop touches it again.
                    break;
                case ValidatedDiscovery vd:
                    UpsertPeer(vd.SenderIP, vd.Packet.Username, vd.Packet.Port,
                               vd.Packet.PublicKeyB64, vd.Packet.RelayIdHash, vd.Packet.Ips,
                               ProtocolCapability.Sanitize(vd.Packet.Caps));
                    break;
            }
        };

        Coordinator.PeerDiscovered += (pkt, ip) =>
            UpsertPeer(ip, pkt.Username, pkt.Port, pkt.PublicKeyB64, pkt.RelayIdHash, pkt.Ips,
                       ProtocolCapability.Sanitize(pkt.Caps));

        Coordinator.PeerDeparted += HandleGoodbye;

        // Every callback below names the conversation by identity key.
        MessagingService.Shared.OnMessageReceived = (peer, entry) =>
        {
            var updated = new Dictionary<string, List<MessageEntry>>(Messages);
            if (!updated.TryGetValue(peer, out var list)) list = updated[peer] = [];
            list.Add(entry);
            Messages = updated;
            // Incoming message from a previously-deleted thread should resurface it.
            if (ConfigStore.Shared.Config.HiddenConversations.Contains(peer))
            {
                ConfigStore.Shared.Config.HiddenConversations.RemoveAll(h => h == peer);
                ConfigStore.Shared.Save();
            }
            RefreshConversations();
            if (entry.Incoming && ShouldShowNotification)
                NotificationService.Shared.ShowMessage(entry.Sender, entry.Text);
        };

        MessagingService.Shared.OnStatusUpdate = (peer, msgId, status) =>
        {
            if (!Messages.TryGetValue(peer, out var list)) return;
            foreach (var e in list.Where(e => e.MessageId == msgId)) e.Status = status;
            // Targeted notification — no full Messages PropertyChanged. ChatPage
            // updates one row in place; Sidebar ignores status updates entirely.
            MessageStatusUpdated?.Invoke(peer, msgId, status);
        };

        MessagingService.Shared.OnDeliveryPathUpdate = msgId =>
        {
            // The message's conversation isn't known to the caller (a relay
            // outbox retry only has the messageId), so scan for it.
            foreach (var list in Messages.Values)
            {
                var entry = list.FirstOrDefault(e => e.MessageId == msgId);
                if (entry is null) continue;
                entry.DeliveryPath = "relay";
                break;
            }
            MessageDeliveryPathUpdated?.Invoke(msgId);
        };

        MessagingService.Shared.OnMessageDeleted = (peer, messageId) =>
        {
            if (!Messages.TryGetValue(peer, out var list)) return;
            var entry = list.FirstOrDefault(e => e.MessageId == messageId);
            if (entry is null) return;
            entry.Deleted          = true;
            entry.Text             = "";
            entry.ReplyToMessageId = null;
            entry.ReplyToPreview   = null;
            entry.ReplyToSender    = null;
            OnPropertyChanged(nameof(Messages));
            RefreshConversations();
        };

        // Inbound edit already applied to HistoryStore by MessagingService;
        // mirror it into the in-memory copy the UI renders from.
        MessagingService.Shared.OnMessageEdited = (peer, messageId, newText, editedAt) =>
        {
            if (!Messages.TryGetValue(peer, out var list)) return;
            var entry = list.FirstOrDefault(e => e.MessageId == messageId);
            if (entry is null) return;
            entry.Text     = newText;
            entry.Edited   = true;
            entry.EditedAt = editedAt;
            OnPropertyChanged(nameof(Messages));
            RefreshConversations();
        };

        MessagingService.Shared.OnTypingUpdate = (peer, sender, active) =>
        {
            var updated = new Dictionary<string, (string, bool)>(TypingStates)
            {
                [peer] = (sender, active)
            };
            TypingStates = updated;
            RefreshConversations();
        };

        FileTransferService.Shared.OnProgress = (peer, label, bytes, total) =>
        {
            var updated = new Dictionary<string, (string, long, long)>(ActiveTransfers)
            {
                [peer] = (label, bytes, total)
            };
            ActiveTransfers = updated;
        };

        FileTransferService.Shared.OnError = (peer, _) =>
        {
            // Clear the in-progress banner so the UI doesn't stay stuck mid-progress
            // (was previously never wired up — mirrors macOS's onError handler).
            var updated = new Dictionary<string, (string, long, long)>(ActiveTransfers);
            updated.Remove(peer);
            ActiveTransfers = updated;
        };

        FileTransferService.Shared.OnComplete = (peer, label, transferId, localPath) =>
        {
            var updated = new Dictionary<string, (string, long, long)>(ActiveTransfers);
            updated.Remove(peer);
            ActiveTransfers = updated;

            // Sender side gets a non-null local path — add an outgoing file bubble.
            if (localPath is null) return;
            var entry = new MessageEntry
            {
                Sender = ConfigStore.Shared.Config.Username,
                Text = $"__FILE__:{localPath}",
                Incoming = false,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                MessageId = transferId,   // stable ID enables receipt matching
                Status = "Sent", ReadReceiptSent = false,
            };
            HistoryStore.Shared.Append(entry, peer);
            HistoryStore.Shared.Save();
            var msgs = new Dictionary<string, List<MessageEntry>>(Messages);
            if (!msgs.TryGetValue(peer, out var l)) l = msgs[peer] = [];
            l.Add(entry);
            Messages = msgs;
            RefreshConversations();
        };

        FileTransferService.Shared.OnIncomingFile = (peer, address, sender, transferId, path) =>
        {
            // Its chunks decrypted under the key, so the device is at `address`.
            NotePeerAddress(peer, address, sender);
            if (ShouldShowNotification)
                NotificationService.Shared.ShowFileReceived(sender, Path.GetFileName(path));
            var entry = new MessageEntry
            {
                Sender = sender, Text = $"__FILE__:{path}", Incoming = true,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                MessageId = transferId,   // stable ID enables read-receipt matching
                Status = "", ReadReceiptSent = false,
            };
            HistoryStore.Shared.Append(entry, peer);
            HistoryStore.Shared.Save();
            var updated = new Dictionary<string, List<MessageEntry>>(Messages);
            if (!updated.TryGetValue(peer, out var list)) list = updated[peer] = [];
            list.Add(entry);
            Messages = updated;
            RefreshConversations();
            // Notify the sender that the file was delivered (→ two grey checks),
            // over the connection's own address: it just came from there.
            MessagingService.Shared.SendReceipt("sent_receipt", transferId, address);
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
}
