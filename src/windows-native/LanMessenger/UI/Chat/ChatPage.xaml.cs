using LanMessenger.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI.Chat;

public sealed class MessageRowViewModel
{
    public string  Sender    { get; init; } = "";
    public string  Text      { get; init; } = "";
    public bool    Incoming  { get; init; }
    public string  Timestamp { get; init; } = "";
    public string  Status    { get; init; } = "";
    public bool    IsFile    { get; init; }
    public string  FilePath  { get; init; } = "";
}

public sealed partial class ChatPage : Page
{
    private AppModel? _model;
    public AppModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelPropertyChanged;
                Composer.Send             += OnSend;
                Composer.TypingChanged    += OnTyping;
                Composer.FilesDropped     += OnFilesDropped;
                Refresh();
            }
        }
    }

    public ChatPage() => InitializeComponent();

    private void OnModelPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Messages)
            or nameof(AppModel.SelectedPeerIP)
            or nameof(AppModel.Peers)
            or nameof(AppModel.TypingStates)
            or nameof(AppModel.ActiveTransfers))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var ip   = _model.SelectedPeerIP;
        var peer = _model.Peers.Values.FirstOrDefault(p => p.IP == ip);

        // Header
        var name = peer?.Username ?? ip;
        HeaderAvatar.NameText = name;
        HeaderName.Text       = name;
        HeaderOnlineDot.Visibility =
            (peer?.IsOnline ?? false) ? Visibility.Visible : Visibility.Collapsed;

        var typing = _model.TypingStates.TryGetValue(ip, out var t) ? t : default;
        HeaderSubtext.Text = typing.Active
            ? $"{typing.Sender} is typing…"
            : ((peer?.IsOnline ?? false) ? "Online" : "Offline");

        // Messages
        var entries = _model.Messages.TryGetValue(ip, out var list) ? list : [];
        var rows = entries.Select(e => MapEntry(e)).ToList();
        MessagesList.ItemsSource = rows;

        // Send read receipts for incoming unread
        foreach (var entry in entries.Where(e => e.Incoming && e.Status == "" && e.MessageId is not null && !e.ReadReceiptSent))
            _model.SendReadReceipt(entry, ip);

        // Banner
        if (_model.ActiveTransfers.TryGetValue(ip, out var xfer))
        {
            TransferBanner.Update(xfer.Label, xfer.Bytes, xfer.Total);
            TransferBanner.Visibility = Visibility.Visible;
        }
        else
        {
            TransferBanner.Visibility = Visibility.Collapsed;
        }

        // Auto-scroll to bottom
        DispatcherQueue.TryEnqueue(() =>
            MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null));
    }

    private static MessageRowViewModel MapEntry(MessageEntry e)
    {
        var isFile = e.Text.StartsWith("__FILE__:");
        var path   = isFile ? e.Text["__FILE__:".Length..] : "";
        var ts     = DateTimeOffset.FromUnixTimeMilliseconds((long)(e.Timestamp * 1000)).LocalDateTime;
        return new MessageRowViewModel
        {
            Sender    = e.Sender,
            Text      = isFile ? Path.GetFileName(path) : e.Text,
            Incoming  = e.Incoming,
            Timestamp = ts.ToString("h:mm tt"),
            Status    = e.Status,
            IsFile    = isFile,
            FilePath  = path,
        };
    }

    private void OnSend(string text)
    {
        if (_model is null || _model.SelectedPeerIP is null) return;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        _model.SendMessage(trimmed, _model.SelectedPeerIP);
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
}
