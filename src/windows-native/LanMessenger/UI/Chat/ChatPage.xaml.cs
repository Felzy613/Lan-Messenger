using LanMessenger.Core.Persistence;
using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace LanMessenger.UI.Chat;

// MessageRowViewModel is a snapshot of a single message for display.
// It implements INotifyPropertyChanged so status updates can mutate just one row
// without forcing the whole list to rebuild — that's what kills scroll position.
public sealed class MessageRowViewModel : INotifyPropertyChanged
{
    public string  Sender    { get; init; } = "";
    public bool    Incoming  { get; init; }
    public string  Timestamp { get; init; } = "";
    public bool    IsFile    { get; init; }
    public string  FilePath  { get; init; } = "";
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? ReplyToPreview   { get; init; }
    public string? ReplyToSender    { get; init; }
    /// Local file path of the replied-to media/file message, if any. Resolved
    /// from conversation history at map time; null for text replies.
    public string? ReplyFilePath    { get; init; }
    /// True when this message was deleted — the bubble renders a placeholder.
    public bool Deleted             { get; init; }
    /// True when this is the first message of a run from one side (i.e. the
    /// previous message, if any, was from the other side). Drives the bubble's
    /// "tail" corner and, for incoming runs, the sender-name label above it —
    /// matches macOS's `isFirstInRun: entry.incoming != prevIncoming`.
    public bool IsFirstInRun         { get; init; }

    // Status and DeliveredViaRelay are mutable — checkmarks and the relay
    // badge update in place without rebuilding the row. DeliveredViaRelay
    // starts false and flips true once the cloud relay Worker confirms an
    // outgoing message's upload (see AppModel.MessageDeliveryPathUpdated) —
    // previously that only ever showed up after the next full history reload.
    private string _status = "";
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    }

    // Text and Edited are mutable for the same reason Status is: an inbound
    // edit_message rewrites an existing message in place. Rebuilding the whole
    // row list to show one changed body would throw away scroll position.
    private string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
    }

    private bool _edited;
    public bool Edited
    {
        get => _edited;
        set { if (_edited != value) { _edited = value; PropertyChanged?.Invoke(this, new(nameof(Edited))); } }
    }

    private bool _deliveredViaRelay;
    public bool DeliveredViaRelay
    {
        get => _deliveredViaRelay;
        set { if (_deliveredViaRelay != value) { _deliveredViaRelay = value; PropertyChanged?.Invoke(this, new(nameof(DeliveredViaRelay))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class ChatPage : Page
{
    private readonly ObservableCollection<MessageRowViewModel> _rows = [];
    private string? _boundPeerIP;
    private ScrollViewer? _scroll;   // inner scroll viewer of MessagesList, cached after layout

    /// Whether the thread is following new content down. Latched rather than
    /// measured on the spot: the extent is still growing when a message is
    /// appended, so a reading taken then says the reader is adrift when they
    /// are not — and a thread that concludes that stops following at all.
    private bool _pinnedToBottom = true;
    /// Re-runs the scroll for a short while after it is asked for. One
    /// ChangeView is not enough: the row has not been measured when the merge
    /// path runs, and an image bubble grows again when its bitmap decodes.
    private DispatcherQueueTimer? _settleTimer;
    private int _settleTicks;
    /// Last extent seen by the scroll handlers. A ViewChanged that lands on a
    /// bigger extent is the content having grown, not the reader having moved
    /// — and the distance-to-bottom it reports is exactly that growth.
    private double _lastExtentHeight;
    private const int SettleTickCount    = 8;    // × 80 ms ≈ 0.64 s
    private const int SettleIntervalMs   = 80;
    private bool IsSettling => _settleTimer?.IsRunning == true;

    private AppModel? _model;
    public AppModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null)
            {
                _model.PropertyChanged    -= OnModelPropertyChanged;
                _model.MessageStatusUpdated -= OnMessageStatusUpdated;
                _model.MessageDeliveryPathUpdated -= OnMessageDeliveryPathUpdated;
                Composer.Send             -= OnSend;
                Composer.TypingChanged    -= OnTyping;
                Composer.AttachRequested  -= OnAttachRequested;
                Composer.FilesPasted      -= SendAttachments;
                Composer.CancelRequested  -= OnComposerCancel;
                Composer.ScreenshotRequested -= OnScreenshotRequested;
            }
            _model = value;
            if (_model is not null)
            {
                _model.PropertyChanged    += OnModelPropertyChanged;
                _model.MessageStatusUpdated += OnMessageStatusUpdated;
                _model.MessageDeliveryPathUpdated += OnMessageDeliveryPathUpdated;
                Composer.Send             += OnSend;
                Composer.TypingChanged    += OnTyping;
                Composer.AttachRequested  += OnAttachRequested;
                // A pasted attachment takes exactly the same route as a dropped one.
                Composer.FilesPasted      += SendAttachments;
                Composer.CancelRequested  += OnComposerCancel;
                Composer.ScreenshotRequested += OnScreenshotRequested;
                RefreshForSelectedPeer(forceReload: true);
            }
        }
    }

    // Direct row update — mirrors OnMessageStatusUpdated. The relay outbox
    // retry only knows the messageId (not which peer/IP it belongs to), so
    // this just scans the currently-bound conversation's rows; if the
    // message belongs to a different conversation, there's simply no match
    // and this is a no-op (the row will show the badge correctly whenever
    // that conversation is next opened, since AppModel already patched the
    // underlying MessageEntry).
    private void OnMessageDeliveryPathUpdated(string msgId)
    {
        foreach (var row in _rows)
        {
            if (row.MessageId == msgId) { row.DeliveredViaRelay = true; break; }
        }
    }

    // Direct row update — no full message-list re-evaluation. Receipts arrive
    // in bursts during cross-platform delivery; using MergeMessages here would
    // touch every row in the chat for each receipt.
    private void OnMessageStatusUpdated(string peerIP, string msgId, string status)
    {
        if (peerIP != _boundPeerIP) return;
        var newRank = StatusRank(status);
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.MessageId == msgId)
            {
                row.Status = status;
            }
            else if (row.IsFile && !row.Incoming && row.MessageId != msgId && newRank > StatusRank(row.Status))
            {
                // Heuristic: promote all outgoing file rows when any message in this
                // conversation gets a higher-ranked acknowledgement. Covers both legacy
                // rows (MessageId == null) and new rows where the receiver hasn't yet
                // sent an individual file receipt (e.g., running an older version).
                row.Status = status;
            }
        }
    }

    private static int StatusRank(string? status) => status switch
    {
        "Queued"    => 0,
        "Sending"   => 1,
        "Sent"      => 2,
        "Delivered" => 3,
        "Read"      => 4,
        _           => -1,
    };

    public MessageEntry? ReplyTarget { get; private set; }
    /// Non-null while the composer is editing an already-sent message instead
    /// of writing a new one.
    public MessageEntry? EditTarget { get; private set; }
    /// The in-progress draft that edit mode displaced, restored on cancel or
    /// after the edit is sent — entering edit mode must not eat what the user
    /// had already typed.
    private string _draftBeforeEdit = "";

    public ChatPage()
    {
        InitializeComponent();
        MessagesList.ItemsSource = _rows;
        WireDropTargets();

        // The thread's dots live in a bubble at message size; the header's are
        // caption-sized to sit where the Online/Offline text does.
        ThreadTyping.UseBubbleShell();
        HeaderTyping.DotDiameter = 5;
        HeaderTyping.DotSpacing  = 3;

        // Cache the inner ScrollViewer once the visual tree is built so we can
        // query scroll position without walking the tree on every message update.
        EventHandler<object>? layoutHandler = null;
        layoutHandler = (_, _) =>
        {
            var sv = FindDescendant<ScrollViewer>(MessagesList);
            if (sv is null) return;
            _scroll = sv;
            _scroll.ViewChanged += OnScrollViewChanged;
            // The ScrollViewer's own content is what grows when a bubble
            // settles — a decoded image, a rewrapped line — and that growth
            // raises no ViewChanged. Without following it the thread is left
            // showing the message but not the picture in it.
            if (_scroll.Content is FrameworkElement scrollContent)
                scrollContent.SizeChanged += OnScrollContentSizeChanged;
            MessagesList.LayoutUpdated -= layoutHandler;
            UpdateJumpToLatest();
        };
        MessagesList.LayoutUpdated += layoutHandler;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    // PropertyChanged handler is small and targeted — only touch what changed.
    private void OnModelPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (_model is null) return;
        switch (e.PropertyName)
        {
            case nameof(AppModel.RemoteSessionRunning):
                // A session ending re-enables the button. Nothing else in the
                // header changes at that moment, so without this it stays
                // greyed out from the first session onward.
                UpdateRemoteDesktopButton();
                break;
            case nameof(AppModel.SelectedPeerIP):
                RefreshForSelectedPeer(forceReload: true);
                break;

            case nameof(AppModel.Messages):
                // Incremental update — preserves scroll/focus.
                MergeMessages();
                break;

            case nameof(AppModel.Peers):
                // Only the header's online dot / subtext depends on Peers.
                UpdateHeaderOnlineState();
                break;

            case nameof(AppModel.TypingStates):
                UpdateTypingIndicator();
                break;

            case nameof(AppModel.ActiveTransfers):
                UpdateTransferBanner();
                break;
        }
    }

    // Full reload — used when switching peers or first attaching to a model.
    private void RefreshForSelectedPeer(bool forceReload)
    {
        if (_model is null) return;
        var ip = _model.SelectedPeerIP;

        // Leave edit mode BEFORE the draft save below. While editing,
        // Composer.Text holds an already-sent message; saving that as the
        // outgoing conversation's draft would resurrect a sent message as an
        // unsent one. SetEditTarget(null) puts the real in-progress draft back.
        SetEditTarget(null);

        // Save the composer's in-progress text as a draft for the conversation
        // we're leaving, then restore (or clear) it for the new one.
        if (_boundPeerIP is not null && _boundPeerIP != ip)
        {
            var draft = Composer.Text;
            if (string.IsNullOrEmpty(draft)) _model.Drafts.Remove(_boundPeerIP);
            else _model.Drafts[_boundPeerIP] = draft;
        }
        if (forceReload || _boundPeerIP != ip)
            Composer.Text = ip is not null && _model.Drafts.TryGetValue(ip, out var d) ? d : "";

        _boundPeerIP = ip;

        // Reset reply state when switching peers.
        SetReplyTarget(null);

        UpdateHeaderName();
        UpdateHeaderOnlineState();
        UpdateTypingIndicator();
        UpdateTransferBanner();

        if (forceReload) _rows.Clear();
        MergeMessages();

        // Send read receipts for any unread incoming messages (clears the badge).
        if (ip is not null) _model.MarkConversationRead(ip);

        // Opening a conversation always lands on the newest message.
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low, ScrollToBottomSettled);
    }

    private void UpdateHeaderName()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var ip = _model.SelectedPeerIP;
        var peer    = _model.Peers.Values.FirstOrDefault(p => p.IP == ip);
        var contact = LanMessenger.Core.Persistence.ConfigStore.Shared.Config.Contacts
            .FirstOrDefault(c => c.LastIP == ip);
        // Fall back to the sender name from the most recent incoming message so that
        // offline peers whose conversation exists in history show their name, not the raw IP.
        string? historyName = null;
        if (_model.Messages.TryGetValue(ip, out var msgs))
            historyName = msgs.LastOrDefault(e => e.Incoming)?.Sender;
        var name = peer?.Username ?? contact?.Username ?? historyName ?? ip;
        HeaderAvatar.NameText = name;
        HeaderName.Text       = name;
        UpdateRemoteDesktopButton();
    }

    /// Greys the screen-control button and says why, using the same policy an
    /// inbound invite is judged by — so the interface can never offer something
    /// the gate would refuse.
    private void UpdateRemoteDesktopButton()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var ip = _model.SelectedPeerIP;
        var peer = _model.Peers.Values.FirstOrDefault(p => p.IP == ip);
        string key = peer?.PublicKeyB64 ?? "";
        string name = HeaderName.Text;

        var availability = _model.RemoteDesktopAvailability(key);
        RemoteDesktopBtn.IsEnabled = availability.IsAvailable;
        ToolTipService.SetToolTip(RemoteDesktopBtn,
                                  AppModel.RemoteDesktopHint(availability, name));
    }

    private void RemoteDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var ip = _model.SelectedPeerIP;
        var peer = _model.Peers.Values.FirstOrDefault(p => p.IP == ip);
        if (peer is null) return;
        // Nothing inside a WinUI event handler may throw.
        try { _model.RequestRemoteDesktop(peer.PublicKeyB64, ip); }
        catch (Exception ex)
        {
            LanMessenger.Core.Services.LanLogger.Remote(
                "error", peer: ip, reason: $"invite from the chat header failed: {ex.Message}");
        }
    }

    private void UpdateHeaderOnlineState()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var ip = _model.SelectedPeerIP;
        var peer = _model.Peers.Values.FirstOrDefault(p => p.IP == ip);
        var online = peer?.IsOnline ?? false;
        // Inline dot after the name: green when online, gray when offline (matches macOS header)
        HeaderNameDot.Fill = online ? Theme.OnlineDotBrush : Theme.OfflineDotBrush;

        HeaderSubtext.Text = online ? "Online" : "Offline";
        UpdateRemoteDesktopButton();
    }

    /// Drives both copies of the typing indicator: the dots under the peer's
    /// name and the bubble at the end of the thread. The bubble grows the
    /// thread, so it follows the same rule an arriving message does — only
    /// scroll down to it if the reader is already at the bottom.
    private void UpdateTypingIndicator()
    {
        if (_model is null) return;
        var ip = _model.SelectedPeerIP;
        var typing = ip is not null
                     && _model.TypingStates.TryGetValue(ip, out var t)
                     && t.Active;
        if (typing == ThreadTyping.IsActive) return;

        var wasAtBottom = _pinnedToBottom;

        ThreadTyping.IsActive    = typing;
        HeaderTyping.IsActive    = typing;
        HeaderSubtext.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;

        if (typing && wasAtBottom)
            DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low, ScrollToBottomSettled);
        else
            DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low, UpdateJumpToLatest);
    }

    private void UpdateTransferBanner()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var ip = _model.SelectedPeerIP;
        if (_model.ActiveTransfers.TryGetValue(ip, out var xfer))
        {
            TransferBanner.Update(xfer.Label, xfer.Bytes, xfer.Total);
            TransferBanner.Visibility = Visibility.Visible;
        }
        else
        {
            TransferBanner.Visibility = Visibility.Collapsed;
        }
    }

    // Merges the model's message list for the current peer into `_rows`:
    // - If the new list is exactly the existing rows plus N appended messages, append only those.
    // - If a row already exists for a given (MessageId, Text) pair, just update its Status.
    // - Otherwise (rare — message deleted or reordered), do a careful full rebuild.
    private void MergeMessages()
    {
        if (_model is null || _boundPeerIP is null) return;
        var entries = _model.Messages.TryGetValue(_boundPeerIP, out var list) ? list : [];

        // Append-only fast path: existing prefix matches.
        var prefixMatches = entries.Count >= _rows.Count;
        if (prefixMatches)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (!SameMessage(_rows[i], entries[i]) || _rows[i].Deleted != entries[i].Deleted)
                {
                    prefixMatches = false;
                    break;
                }
            }
        }

        if (prefixMatches)
        {
            // Update statuses — and edited bodies — for existing rows in place.
            for (var i = 0; i < _rows.Count; i++)
            {
                _rows[i].Status = MapStatus(entries[i].Status);
                _rows[i].Text   = FormatRowText(entries[i]);
                _rows[i].Edited = entries[i].Edited;
            }

            // Append new ones.
            var wasAtBottom = _pinnedToBottom;
            var rowsBefore  = _rows.Count;
            for (var i = _rows.Count; i < entries.Count; i++)
                _rows.Add(MapEntry(entries, i));
            var appended = _rows.Count > rowsBefore;

            // Sending always jumps to the newest message, the way opening a
            // conversation does; receiving only follows when the reader was
            // already at the bottom. Gated on something actually having been
            // appended — this method also runs for status-only and edit-only
            // updates, and those must not move the thread.
            var sentByUs = appended && entries.Count > 0 && !entries[^1].Incoming;
            if (appended && (wasAtBottom || sentByUs))
                DispatcherQueue.TryEnqueue(
                    DispatcherQueuePriority.Low, ScrollToBottomSettled);
            else
                // Reading back through history, or nothing new to show: the
                // thread stays put and the jump button is what says there is
                // something newer below. A bubble that grows in place here is
                // picked up by OnScrollContentSizeChanged.
                DispatcherQueue.TryEnqueue(
                    DispatcherQueuePriority.Low, UpdateJumpToLatest);

            // Auto-read newly-arrived incoming messages only while the window is
            // visible — messages that arrive after the user hides to tray should
            // not be silently marked read.
            if (_model.IsWindowVisible && entries.Any(e => e.Incoming && !e.ReadReceiptSent))
                _model.MarkConversationRead(_boundPeerIP);
            return;
        }

        // Fallback — rebuild but try to preserve scroll position.
        var verticalOffset = _scroll?.VerticalOffset ?? 0;
        _rows.Clear();
        for (var i = 0; i < entries.Count; i++) _rows.Add(MapEntry(entries, i));
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _scroll?.ChangeView(null, verticalOffset, null, disableAnimation: true);
            UpdateJumpToLatest();
        });
    }

    /// Scrolls to the newest message and keeps doing so for a moment
    /// afterwards, while the rows that triggered it finish laying out.
    ///
    /// A single ChangeView regularly lands short: the appended row has not
    /// been measured yet when the merge path runs, so ScrollableHeight is
    /// still the old one, and a bubble holding an image grows again when the
    /// bitmap decodes. Repeating the scroll for ~0.6 s covers both without
    /// having to know which is happening.
    private void ScrollToBottomSettled()
    {
        _pinnedToBottom = true;
        _settleTicks    = 0;

        _settleTimer ??= CreateSettleTimer();
        _settleTimer.Stop();          // restart the window from now
        // Started before the first scroll, not after: ChangeView can raise
        // ViewChanged synchronously, and a reading taken then — with the
        // extent still growing — would unpin us and stop the settle on its
        // very first tick.
        _settleTimer.Start();
        ScrollToBottom();
    }

    private DispatcherQueueTimer CreateSettleTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(SettleIntervalMs);
        timer.Tick += (t, _) =>
        {
            if (!_pinnedToBottom || ++_settleTicks >= SettleTickCount)
            {
                t.Stop();
                UpdateJumpToLatest();
                return;
            }
            ScrollToBottom();
        };
        return timer;
    }

    private void StopSettling()
    {
        _settleTimer?.Stop();
    }

    private void ScrollToBottom()
    {
        if (_scroll is not null)
            _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true);
        else if (_rows.Count > 0)
            MessagesList.ScrollIntoView(_rows[^1]);
        UpdateJumpToLatest();
    }

    private bool IsScrolledToBottom()
    {
        if (_scroll is null) return true;
        return _scroll.ScrollableHeight <= 0
            || (_scroll.ScrollableHeight - _scroll.VerticalOffset) < 40;
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // A wheel or drag from the user outranks a settle in progress: they
        // asked to be somewhere else, so stop putting them back.
        if (e.IsIntermediate) StopSettling();

        var extent = _scroll?.ExtentHeight ?? 0;
        var grew   = extent > _lastExtentHeight + 0.5;
        _lastExtentHeight = extent;

        // Only a reading taken at rest, with the content the size it already
        // was, gets to decide the reader has scrolled away — mid-settle, or
        // right after a bubble grew, the extent is moving under them.
        if (!IsSettling && !grew) _pinnedToBottom = IsScrolledToBottom();
        UpdateJumpToLatest();
    }

    private void OnScrollContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= e.PreviousSize.Height + 0.5) return;
        _lastExtentHeight = _scroll?.ExtentHeight ?? _lastExtentHeight;
        if (!_pinnedToBottom || IsSettling) return;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low, ScrollToBottomSettled);
    }

    /// Called when the window comes back from the tray or a minimize. The
    /// page is never unloaded in either case, so nothing else re-runs the
    /// "open a conversation, land on the newest message" step.
    public void OnWindowShown()
    {
        if (!_pinnedToBottom) return;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low, ScrollToBottomSettled);
    }

    // ── Jump to latest ───────────────────────────────────────────────────────

    /// Shows the floating jump button exactly while the newest message is out of
    /// view. ScrollViewer.ViewChanged covers scrolling and resizing; appending
    /// rows grows the extent without necessarily raising it, so the merge path
    /// calls this too.
    private void UpdateJumpToLatest()
    {
        // Hidden while a scroll is settling too: the extent is mid-flight
        // there and would flash the button on for a frame or two.
        var show = _rows.Count > 0 && !IsSettling && !IsScrolledToBottom();
        JumpToLatestBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void JumpToLatestBtn_Click(object sender, RoutedEventArgs e) => ScrollToBottomSettled();

    private static bool SameMessage(MessageRowViewModel row, MessageEntry entry)
    {
        // Treat rows as equal when their stable identifiers match.
        // For messages without a MessageId (legacy file system messages), fall back to timestamp+text.
        if (row.MessageId is not null && entry.MessageId is not null)
            return row.MessageId == entry.MessageId;
        return row.Text == FormatRowText(entry) && row.Timestamp == FormatTimestamp(entry.Timestamp);
    }

    private static string FormatTimestamp(double unix) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(unix * 1000)).LocalDateTime.ToString("h:mm tt");

    private static string FormatRowText(MessageEntry e)
    {
        var isFile = e.Text.StartsWith("__FILE__:");
        return isFile ? Path.GetFileName(e.Text["__FILE__:".Length..]) : e.Text;
    }

    private static string MapStatus(string raw) => raw;  // pass through; bubble interprets it

    private static MessageRowViewModel MapEntry(IReadOnlyList<MessageEntry> allEntries, int index)
    {
        var e = allEntries[index];
        var isFile = e.Text.StartsWith("__FILE__:");
        var path   = isFile ? e.Text["__FILE__:".Length..] : "";

        // Resolve the file path of the replied-to message so the bubble can
        // show a thumbnail instead of plain text in the reply chip.
        string? replyFilePath = null;
        if (e.ReplyToMessageId is { Length: > 0 } replyId)
        {
            var orig = allEntries.FirstOrDefault(x => x.MessageId == replyId);
            if (orig is not null && orig.Text.StartsWith("__FILE__:"))
                replyFilePath = orig.Text["__FILE__:".Length..];
        }

        var prevIncoming = index > 0 ? allEntries[index - 1].Incoming : !e.Incoming;

        return new MessageRowViewModel
        {
            Sender    = e.Sender,
            Text      = isFile ? Path.GetFileName(path) : e.Text,
            Incoming  = e.Incoming,
            Timestamp = FormatTimestamp(e.Timestamp),
            Status    = e.Status,
            IsFile    = isFile,
            FilePath  = path,
            MessageId = e.MessageId,
            ReplyToMessageId  = e.ReplyToMessageId,
            ReplyToPreview    = e.ReplyToPreview,
            ReplyToSender     = e.ReplyToSender,
            ReplyFilePath     = replyFilePath,
            DeliveredViaRelay = e.DeliveryPath == "relay",
            Deleted           = e.Deleted,
            Edited            = e.Edited,
            IsFirstInRun      = e.Incoming != prevIncoming,
        };
    }

    // MARK: - Composer events

    private void OnSend(string text)
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;

        if (EditTarget is { } editing)
        {
            // Leave the composer in edit mode if the message turned out not to
            // be editable, rather than silently discarding what was typed.
            if (!_model.EditMessage(editing, trimmed, _model.SelectedPeerIP))
            {
                LanLogger.Warn("Edit", "message no longer editable — keeping composer in edit mode");
                Composer.Text = trimmed;
                return;
            }
            SetEditTarget(null);   // restores _draftBeforeEdit and the send glyph
            return;
        }

        // If we're replying, find the original entry in the model's message list.
        MessageEntry? replyTo = null;
        if (ReplyTarget is not null) replyTo = ReplyTarget;

        _model.SendMessage(trimmed, _model.SelectedPeerIP, replyTo);
        _model.Drafts.Remove(_model.SelectedPeerIP);
        SetReplyTarget(null);
    }

    private void OnComposerCancel()
    {
        if (EditTarget is not null) SetEditTarget(null);
        else if (ReplyTarget is not null) SetReplyTarget(null);
    }

    private void OnTyping(bool active)
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        _model.SendTyping(active, _model.SelectedPeerIP);
    }

    // Synchronous Win32 file picker — replaces the WinRT FileOpenPicker that
    // threw COMException 0x80004005 in some unpackaged-app configurations.
    // GetOpenFileNameW bypasses the shell-broker COM surrogate entirely and is
    // reliable regardless of package identity or window activation state.
    // GetOpenFileName pumps its own inner message loop while the dialog is
    // open, so the UI thread stays responsive even though this is synchronous.
    private void OnAttachRequested()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var targetPeerIP = _model.SelectedPeerIP;

        Composer.IsAttachmentPickerOpen = true;
        try
        {
            if (Application.Current is not global::LanMessenger.App app || app.MainWindow is null)
            {
                LanLogger.Error("Attachment", "MainWindow unavailable — cannot open file picker.");
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app.MainWindow);
            if (hwnd == IntPtr.Zero)
            {
                LanLogger.Error("Attachment", "GetWindowHandle returned zero — cannot open file picker.");
                return;
            }

            // Bring the window to the foreground so the dialog appears on top.
            app.MainWindow.ShowWindowFromTray();

            var files = Core.Services.Win32FileDialog.PickMultipleFiles(hwnd);
            foreach (var path in files)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    _model.SendFile(path, targetPeerIP);
            }
        }
        catch (Exception ex)
        {
            LanLogger.Error("Attachment", $"File picker error: {ex.GetType().Name}: {ex.Message}", ex);
        }
        finally
        {
            Composer.IsAttachmentPickerOpen = false;
        }
    }

    /// The single exit for every attachment route into this page — page drop,
    /// composer paste, and (via ChatPage's own picker/screenshot flows) the
    /// paperclip and camera buttons.
    private void SendAttachments(IReadOnlyList<string> paths)
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        LanLogger.Info("Attachment", $"sending {paths.Count} attachment(s) to {_model.SelectedPeerIP}");
        foreach (var p in paths)
            _model.SendFile(p, _model.SelectedPeerIP);
    }

    // ── Thread-wide file drop ────────────────────────────────────────────────

    /// <summary>
    /// Registers the drag handlers on the thread's big child surfaces as well as
    /// on the page root.
    ///
    /// The root Grid declares AllowDrop and the handlers in XAML, and WinUI's
    /// drag events bubble — but the ListView covers nearly the whole thread and
    /// is a control with its own class handling for drag (it uses these very
    /// events for item reorder), so an event it marks handled never reaches the
    /// Grid's handler. Registering on the children directly, with
    /// handledEventsToo, takes that question off the table. The composer gets the
    /// same treatment so the strip along the bottom is a target too: its TextBox
    /// sets AllowDrop="False" precisely so drops land here instead of being
    /// swallowed as text insertion.
    /// </summary>
    private void WireDropTargets()
    {
        foreach (var target in new UIElement[] { MessagesList, Composer })
        {
            target.AllowDrop = true;
            target.AddHandler(DragEnterEvent, new DragEventHandler(Page_DragEnter), true);
            target.AddHandler(DragOverEvent,  new DragEventHandler(Page_DragOver),  true);
            target.AddHandler(DragLeaveEvent, new DragEventHandler(Page_DragLeave), true);
            target.AddHandler(DropEvent,      new DragEventHandler(Page_Drop),      true);
        }
    }

    /// Ticks on every DragOver so a deferred DragLeave can tell "the pointer
    /// really left the thread" from "the pointer crossed between two children".
    private int  _dragOverTick;
    /// One log line per drag session rather than one per event.
    private bool _dragSessionLogged;
    private bool _dropOverlayShown;

    private void Page_DragEnter(object sender, DragEventArgs e) => AcceptFileDrag(e);

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        _dragOverTick++;
        AcceptFileDrag(e);
    }

    /// <summary>
    /// Shared by DragEnter and DragOver. Everything here is synchronous by
    /// design: awaiting inside a drag handler returns control to the drag
    /// source, which reads AcceptedOperation at that moment and takes the
    /// not-yet-assigned value as a refusal — the drop is then never offered, and
    /// the data object is left in a state that breaks subsequent drags too
    /// (microsoft-ui-xaml#8108). Inspect the payload in Drop, never here.
    /// </summary>
    private void AcceptFileDrag(DragEventArgs e)
    {
        NoteDragSession(e);
        if (_model?.SelectedPeerIP is null) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        // Suppress the shell's own "Copy to ..." caption, which names the app
        // rather than the person the file is about to be sent to. Both the null
        // check and the catch are load-bearing: some drag sources offer no
        // overridable UI, and touching the override on others throws a bare
        // COMException (microsoft-ui-xaml#9296). Neither may escape — an
        // unhandled throw out of a drag handler takes the process down.
        try
        {
            if (e.DragUIOverride is { } ui) ui.IsCaptionVisible = false;
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Attachment", $"drag caption override failed: {ex.GetType().Name}: {ex.Message}");
        }

        e.Handled = true;
        ShowDropOverlay(true);
    }

    /// Records what a drag is actually carrying, once per drag. When a drop
    /// "does nothing", this line is the difference between "no drag event ever
    /// reached the app" (nothing logged — a Windows-side block such as an
    /// elevated process, since Explorer will not hand a drag up an integrity
    /// level) and "the package held no files".
    private void NoteDragSession(DragEventArgs e)
    {
        if (_dragSessionLogged) return;
        _dragSessionLogged = true;
        LanLogger.Info("Attachment",
            $"drag entered thread: files={e.DataView.Contains(StandardDataFormats.StorageItems)}, formats=[{FormatsOf(e)}]");
    }

    /// Never throws: this only ever feeds a log line, and an exception escaping
    /// a drag handler ends the process.
    private static string FormatsOf(DragEventArgs e)
    {
        try { return string.Join(", ", e.DataView.AvailableFormats); }
        catch (Exception ex) { return $"<unreadable: {ex.GetType().Name}>"; }
    }

    private void Page_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when the pointer crosses from one child of the
        // thread to another, because these events bubble — hiding the overlay
        // straight away makes it blink as the cursor moves between the list and
        // the composer. Defer the hide by one dispatcher turn and skip it if a
        // DragOver landed in the meantime, which it will have unless the pointer
        // genuinely left.
        var seen = _dragOverTick;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_dragOverTick != seen) return;
            EndDragSession();
        });
    }

    private void EndDragSession()
    {
        _dragSessionLogged = false;
        ShowDropOverlay(false);
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        EndDragSession();
        if (_model?.SelectedPeerIP is null) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            LanLogger.Warn("Attachment", $"drop carried no files; formats=[{FormatsOf(e)}]");
            return;
        }
        e.Handled = true;

        // The DataView is only guaranteed to outlive the handler if a deferral
        // is held — GetStorageItemsAsync yields, so take one. Awaiting is safe
        // *here*, unlike in DragOver: by the time Drop fires the source no
        // longer needs an answer about whether the drop is accepted.
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<Windows.Storage.IStorageFile>()
                             .Select(f => f.Path)
                             .Where(p => !string.IsNullOrEmpty(p))
                             .ToList();
            if (paths.Count > 0) SendAttachments(paths);
            else
                LanLogger.Warn("Attachment",
                    $"drop produced no usable file paths from {items.Count} item(s) — folders are not sent");
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Attachment", $"drop failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowDropOverlay(bool visible)
    {
        // Guarded because DragOver fires many times a second: re-assigning the
        // caption and Visibility on every tick means a layout pass on every tick.
        if (visible == _dropOverlayShown) return;
        _dropOverlayShown = visible;
        if (visible) DropOverlayText.Text = $"Drop to send to {HeaderName.Text}";
        DropOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Screenshot flow:
    ///   1. Show a window picker so the user selects which window (or full
    ///      screen) to capture.
    ///   2. Capture off the UI thread.
    ///   3. Show a preview dialog — the user must click "Send" explicitly.
    ///      If they cancel, the temp file is deleted.
    /// </summary>
    private async void OnScreenshotRequested()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var targetPeerIP = _model.SelectedPeerIP;

        Composer.IsScreenshotBusy = true;
        string? capturedPath = null;
        try
        {
            // Step 1 — window picker.
            var picker = new ScreenshotWindowPickerDialog { XamlRoot = XamlRoot };
            var pickerResult = await picker.ShowAsync();
            if (pickerResult != ContentDialogResult.Primary)
                return;   // user cancelled the picker

            // Step 2 — capture (off UI thread).
            var hwnd = picker.SelectedHwnd;
            if (hwnd == ScreenshotWindowPickerDialog.SelectRegionSentinel)
            {
                // Drag-to-select: capture the whole primary display first, then
                // let the user crop it with the overlay window.
                var fullPath = await Core.Services.ScreenshotService.CapturePrimaryDisplayAsync();
                var overlay = new RegionSelectOverlayWindow(fullPath);
                var region = await overlay.SelectAsync();
                switch (region.Outcome)
                {
                    case RegionSelectOutcome.Region:
                        capturedPath = await Core.Services.ScreenshotService.CropToRegionAsync(fullPath, region.Region!.Value);
                        break;
                    case RegionSelectOutcome.FullDisplay:
                        capturedPath = fullPath;   // no drag — use the full-display capture as-is
                        break;
                    case RegionSelectOutcome.Cancelled:
                        // User pressed Escape — abandon entirely, don't show a preview.
                        try { File.Delete(fullPath); } catch { }
                        return;
                }
            }
            else
            {
                capturedPath = hwnd == IntPtr.Zero
                    ? await Core.Services.ScreenshotService.CapturePrimaryDisplayAsync()
                    : await Core.Services.ScreenshotService.CaptureWindowAsync(hwnd);
            }

            // Step 3 — preview: user must explicitly click Send.
            var preview = new ScreenshotPreviewDialog(capturedPath) { XamlRoot = XamlRoot };
            var previewResult = await preview.ShowAsync();

            if (previewResult == ContentDialogResult.Primary)
            {
                _model.SendFile(capturedPath, targetPeerIP);
                capturedPath = null;   // ownership transferred; don't delete
            }
            // else: user cancelled — fall through to finally which deletes the file
        }
        catch (Core.Services.ScreenshotService.ScreenshotException ex)
        {
            LanMessenger.Core.Services.LanLogger.Warn("Screenshot", $"capture failed: {ex.Message}");
            await ShowErrorAsync("Screenshot failed", ex.Message);
        }
        catch (Exception ex)
        {
            LanMessenger.Core.Services.LanLogger.Error("Screenshot", "unexpected screenshot error", ex);
            await ShowErrorAsync("Screenshot failed", ex.Message);
        }
        finally
        {
            Composer.IsScreenshotBusy = false;
            // Delete the temp file if it was captured but not sent (user cancelled preview).
            if (capturedPath is not null)
            {
                try { File.Delete(capturedPath); } catch { }
            }
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            };
            _ = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            LanMessenger.Core.Services.LanLogger.Warn("ChatPage", $"error dialog failed: {ex.Message}");
        }
    }

    // MARK: - Reply target

    public void SetReplyTarget(MessageEntry? entry)
    {
        if (entry is not null) SetEditTarget(null);
        ReplyTarget = entry;
        if (entry is null)
        {
            if (EditTarget is null) ReplyBanner.Visibility = Visibility.Collapsed;
            return;
        }
        ReplyBanner.Visibility = Visibility.Visible;
        ReplyBannerWho.Text     = "Replying to " + (entry.Incoming ? entry.Sender : "yourself");
        ReplyBannerPreview.Text = LanMessenger.Core.Services.MessagingService.ReplyPreviewText(entry);
    }

    // MARK: - Edit target

    /// Swaps the composer into (or out of) edit mode. The banner is shared with
    /// reply mode — the two are mutually exclusive, so one strip serves both.
    public void SetEditTarget(MessageEntry? entry)
    {
        if (entry is not null && ReplyTarget is not null)
        {
            ReplyTarget = null;   // direct, not via SetReplyTarget: avoid mutual recursion
        }

        if (entry is null)
        {
            if (EditTarget is not null)
            {
                Composer.Text = _draftBeforeEdit;
                _draftBeforeEdit = "";
            }
            EditTarget = null;
            Composer.IsEditing = false;   // restore the send glyph on every exit path
            if (ReplyTarget is null) ReplyBanner.Visibility = Visibility.Collapsed;
            return;
        }

        if (EditTarget is null) _draftBeforeEdit = Composer.Text;
        EditTarget = entry;
        Composer.Text = entry.Text;
        Composer.IsEditing = true;
        ReplyBanner.Visibility  = Visibility.Visible;
        ReplyBannerWho.Text     = "Editing message";
        ReplyBannerPreview.Text = LanMessenger.Core.Services.MessagingService.ReplyPreviewText(entry);
    }

    // Called by MessageBubbleControl's "Edit" menu item.
    internal void RequestEditMessage(string? messageId)
    {
        if (_model is null || _boundPeerIP is null || messageId is null) return;
        var entries = _model.Messages.TryGetValue(_boundPeerIP, out var list) ? list : [];
        var target = entries.FirstOrDefault(e => e.MessageId == messageId);
        if (target is null) return;
        SetEditTarget(target);
    }

    // Called by MessageBubbleControl via its RequestReply event hook.
    internal void RequestReplyTo(string? messageId)
    {
        if (_model is null || _boundPeerIP is null || messageId is null) return;
        var entries = _model.Messages.TryGetValue(_boundPeerIP, out var list) ? list : [];
        var target = entries.FirstOrDefault(e => e.MessageId == messageId);
        SetReplyTarget(target);
    }

    private void CancelReplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (EditTarget is not null) SetEditTarget(null);
        else SetReplyTarget(null);
    }

    // MARK: - Delete

    // Called by MessageBubbleControl's "Delete for me" / "Delete for everyone" menu items.
    internal void RequestDeleteMessage(string? messageId, bool incoming, string? text, bool isFile, string filePath, string timestamp, bool forEveryone)
    {
        if (_model is null || _boundPeerIP is null) return;
        if (forEveryone && incoming) return;   // can only delete-for-everyone your own messages

        var entries = _model.Messages.TryGetValue(_boundPeerIP, out var list) ? list : [];
        MessageEntry? target = null;
        if (messageId is not null)
            target = entries.FirstOrDefault(e => e.MessageId == messageId);
        if (target is null)
        {
            // Fall back to matching by the same heuristic used elsewhere for
            // entries without a stable MessageId (legacy file messages).
            var rowText = isFile ? "__FILE__:" + filePath : text ?? "";
            target = entries.FirstOrDefault(e =>
                e.MessageId is null &&
                e.Incoming == incoming &&
                e.Text == rowText &&
                FormatTimestamp(e.Timestamp) == timestamp);
        }
        if (target is null) return;

        if (forEveryone && target.Incoming) return;

        _model.DeleteMessage(target, _boundPeerIP, forEveryone);
    }
}
