using LanMessenger.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace LanMessenger.UI.Chat;

// MessageRowViewModel is a snapshot of a single message for display.
// It implements INotifyPropertyChanged so status updates can mutate just one row
// without forcing the whole list to rebuild — that's what kills scroll position.
public sealed class MessageRowViewModel : INotifyPropertyChanged
{
    public string  Sender    { get; init; } = "";
    public string  Text      { get; init; } = "";
    public bool    Incoming  { get; init; }
    public string  Timestamp { get; init; } = "";
    public bool    IsFile    { get; init; }
    public string  FilePath  { get; init; } = "";
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? ReplyToPreview   { get; init; }
    public string? ReplyToSender    { get; init; }

    // Status is the one mutable field — checkmarks update without rebuilding the row.
    private string _status = "";
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class ChatPage : Page
{
    // Rows are owned by the page and bound ONCE; we mutate the collection
    // instead of reassigning ItemsSource so scroll position and focus survive.
    private readonly ObservableCollection<MessageRowViewModel> _rows = [];
    private string? _boundPeerIP;

    private AppModel? _model;
    public AppModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null) _model.PropertyChanged -= OnModelPropertyChanged;
            _model = value;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelPropertyChanged;
                Composer.Send             += OnSend;
                Composer.TypingChanged    += OnTyping;
                Composer.FilesDropped     += OnFilesDropped;
                RefreshForSelectedPeer(forceReload: true);
            }
        }
    }

    public MessageEntry? ReplyTarget { get; private set; }

    public ChatPage()
    {
        InitializeComponent();
        MessagesList.ItemsSource = _rows;
    }

    // PropertyChanged handler is small and targeted — only touch what changed.
    private void OnModelPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (_model is null) return;
        switch (e.PropertyName)
        {
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
                UpdateHeaderOnlineState();
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
        _boundPeerIP = ip;

        // Reset reply state when switching peers.
        SetReplyTarget(null);

        UpdateHeaderName();
        UpdateHeaderOnlineState();
        UpdateTransferBanner();

        if (forceReload) _rows.Clear();
        MergeMessages();

        // Send read receipts for any unread incoming messages (clears the badge).
        if (ip is not null) _model.MarkConversationRead(ip);

        // Scroll to the latest message after layout settles.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, disableAnimation: true));
    }

    private void UpdateHeaderName()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var ip = _model.SelectedPeerIP;
        var peer = _model.Peers.Values.FirstOrDefault(p => p.IP == ip);
        var contact = LanMessenger.Core.Persistence.ConfigStore.Shared.Config.Contacts
            .FirstOrDefault(c => c.LastIP == ip);
        var name = peer?.Username ?? contact?.Username ?? ip;
        HeaderAvatar.NameText = name;
        HeaderName.Text       = name;
    }

    private void UpdateHeaderOnlineState()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var ip = _model.SelectedPeerIP;
        var peer = _model.Peers.Values.FirstOrDefault(p => p.IP == ip);
        var online = peer?.IsOnline ?? false;
        HeaderOnlineDot.Visibility = online ? Visibility.Visible : Visibility.Collapsed;

        var typing = _model.TypingStates.TryGetValue(ip, out var t) ? t : default;
        HeaderSubtext.Text = typing.Active
            ? $"{typing.Sender} is typing…"
            : (online ? "Online" : "Offline");
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
                if (!SameMessage(_rows[i], entries[i])) { prefixMatches = false; break; }
            }
        }

        if (prefixMatches)
        {
            // Update statuses for existing rows in place.
            for (var i = 0; i < _rows.Count; i++)
                _rows[i].Status = MapStatus(entries[i].Status);

            // Append new ones.
            var wasAtBottom = IsScrolledToBottom();
            for (var i = _rows.Count; i < entries.Count; i++)
                _rows.Add(MapEntry(entries[i]));

            if (wasAtBottom)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null));

            // Auto-read any newly-arrived incoming messages for the open chat.
            if (entries.Any(e => e.Incoming && !e.ReadReceiptSent))
                _model.MarkConversationRead(_boundPeerIP);
            return;
        }

        // Fallback — rebuild but try to preserve scroll position.
        var verticalOffset = MessagesScroll.VerticalOffset;
        _rows.Clear();
        foreach (var e in entries) _rows.Add(MapEntry(e));
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            MessagesScroll.ChangeView(null, verticalOffset, null, disableAnimation: true));
    }

    private bool IsScrolledToBottom()
    {
        // Consider "at bottom" if within ~40px of the bottom (covers small composer overlap).
        return MessagesScroll.ScrollableHeight <= 0
            || (MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset) < 40;
    }

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

    private static MessageRowViewModel MapEntry(MessageEntry e)
    {
        var isFile = e.Text.StartsWith("__FILE__:");
        var path   = isFile ? e.Text["__FILE__:".Length..] : "";
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
            ReplyToMessageId = e.ReplyToMessageId,
            ReplyToPreview   = e.ReplyToPreview,
            ReplyToSender    = e.ReplyToSender,
        };
    }

    // MARK: - Composer events

    private void OnSend(string text)
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;

        // If we're replying, find the original entry in the model's message list.
        MessageEntry? replyTo = null;
        if (ReplyTarget is not null) replyTo = ReplyTarget;

        _model.SendMessage(trimmed, _model.SelectedPeerIP, replyTo);
        SetReplyTarget(null);
    }

    private void OnTyping(bool active)
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        _model.SendTyping(active, _model.SelectedPeerIP);
    }

    private void OnFilesDropped(IReadOnlyList<string> paths)
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        foreach (var p in paths)
            _model.SendFile(p, _model.SelectedPeerIP);
    }

    // MARK: - Reply target

    public void SetReplyTarget(MessageEntry? entry)
    {
        ReplyTarget = entry;
        if (entry is null)
        {
            ReplyBanner.Visibility = Visibility.Collapsed;
            return;
        }
        ReplyBanner.Visibility = Visibility.Visible;
        ReplyBannerWho.Text     = "Replying to " + (entry.Incoming ? entry.Sender : "yourself");
        ReplyBannerPreview.Text = LanMessenger.Core.Services.MessagingService.ReplyPreviewText(entry);
    }

    // Called by MessageBubbleControl via its RequestReply event hook.
    internal void RequestReplyTo(string? messageId)
    {
        if (_model is null || _boundPeerIP is null || messageId is null) return;
        var entries = _model.Messages.TryGetValue(_boundPeerIP, out var list) ? list : [];
        var target = entries.FirstOrDefault(e => e.MessageId == messageId);
        SetReplyTarget(target);
    }

    private void CancelReplyBtn_Click(object sender, RoutedEventArgs e) => SetReplyTarget(null);
}
