using LanMessenger.Core.Persistence;
using LanMessenger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.UI;

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
    /// Local file path of the replied-to media/file message, if any. Resolved
    /// from conversation history at map time; null for text replies.
    public string? ReplyFilePath    { get; init; }
    /// True when this message transited the cloud relay Worker (not direct LAN delivery).
    public bool DeliveredViaRelay   { get; init; }
    /// True when this message was deleted — the bubble renders a placeholder.
    public bool Deleted             { get; init; }

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
    private readonly ObservableCollection<MessageRowViewModel> _rows = [];
    private string? _boundPeerIP;
    private ScrollViewer? _scroll;   // inner scroll viewer of MessagesList, cached after layout

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
                Composer.Send             -= OnSend;
                Composer.TypingChanged    -= OnTyping;
                Composer.AttachRequested  -= OnAttachRequested;
                Composer.FilesDropped     -= OnFilesDropped;
                Composer.ScreenshotRequested -= OnScreenshotRequested;
            }
            _model = value;
            if (_model is not null)
            {
                _model.PropertyChanged    += OnModelPropertyChanged;
                _model.MessageStatusUpdated += OnMessageStatusUpdated;
                Composer.Send             += OnSend;
                Composer.TypingChanged    += OnTyping;
                Composer.AttachRequested  += OnAttachRequested;
                Composer.FilesDropped     += OnFilesDropped;
                Composer.ScreenshotRequested += OnScreenshotRequested;
                RefreshForSelectedPeer(forceReload: true);
            }
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

    public ChatPage()
    {
        InitializeComponent();
        MessagesList.ItemsSource = _rows;

        // Cache the inner ScrollViewer once the visual tree is built so we can
        // query scroll position without walking the tree on every message update.
        EventHandler<object>? layoutHandler = null;
        layoutHandler = (_, _) =>
        {
            var sv = FindDescendant<ScrollViewer>(MessagesList);
            if (sv is null) return;
            _scroll = sv;
            MessagesList.LayoutUpdated -= layoutHandler;
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
        UpdateTransferBanner();

        if (forceReload) _rows.Clear();
        MergeMessages();

        // Send read receipts for any unread incoming messages (clears the badge).
        if (ip is not null) _model.MarkConversationRead(ip);

        // Scroll to the latest message after layout settles.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollToBottom);
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
    }

    private void UpdateHeaderOnlineState()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var ip = _model.SelectedPeerIP;
        var peer = _model.Peers.Values.FirstOrDefault(p => p.IP == ip);
        var online = peer?.IsOnline ?? false;
        // Inline dot after the name: green when online, gray when offline (matches macOS header)
        HeaderNameDot.Fill = new SolidColorBrush(online
            ? Color.FromArgb(255, 37, 211, 102)   // #25D366
            : Color.FromArgb(255, 160, 160, 160)); // gray

        var typing = _model.TypingStates.TryGetValue(ip, out var t) ? t : default;
        HeaderSubtext.Text = typing.Active
            ? "typing..."
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
                if (!SameMessage(_rows[i], entries[i]) || _rows[i].Deleted != entries[i].Deleted)
                {
                    prefixMatches = false;
                    break;
                }
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
                _rows.Add(MapEntry(entries[i], entries));

            if (wasAtBottom)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollToBottom);

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
        foreach (var e in entries) _rows.Add(MapEntry(e, entries));
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            _scroll?.ChangeView(null, verticalOffset, null, disableAnimation: true));
    }

    private void ScrollToBottom()
    {
        if (_scroll is not null)
            _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true);
        else if (_rows.Count > 0)
            MessagesList.ScrollIntoView(_rows[^1]);
    }

    private bool IsScrolledToBottom()
    {
        if (_scroll is null) return true;
        return _scroll.ScrollableHeight <= 0
            || (_scroll.ScrollableHeight - _scroll.VerticalOffset) < 40;
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

    private static MessageRowViewModel MapEntry(MessageEntry e, IReadOnlyList<MessageEntry>? allEntries = null)
    {
        var isFile = e.Text.StartsWith("__FILE__:");
        var path   = isFile ? e.Text["__FILE__:".Length..] : "";

        // Resolve the file path of the replied-to message so the bubble can
        // show a thumbnail instead of plain text in the reply chip.
        string? replyFilePath = null;
        if (e.ReplyToMessageId is { Length: > 0 } replyId && allEntries is not null)
        {
            var orig = allEntries.FirstOrDefault(x => x.MessageId == replyId);
            if (orig is not null && orig.Text.StartsWith("__FILE__:"))
                replyFilePath = orig.Text["__FILE__:".Length..];
        }

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
        _model.Drafts.Remove(_model.SelectedPeerIP);
        SetReplyTarget(null);
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

    private void OnFilesDropped(IReadOnlyList<string> paths)
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        foreach (var p in paths)
            _model.SendFile(p, _model.SelectedPeerIP);
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
