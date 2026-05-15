using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace LanMessenger.UI.Sidebar;

public sealed partial class ArchivedPage : Page
{
    // Fired when the user clicks the Back button — the host swaps the content frame
    // back to whatever was visible before the archived page was opened.
    public event Action? BackRequested;
    // Fired when the user opens an archived thread — host should swap to the chat page.
    public event Action<string>? ConversationOpened;

    private AppModel? _model;
    public AppModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = value;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
                Refresh();
            }
        }
    }

    public ArchivedPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void OnModelChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.ArchivedConversations) or nameof(AppModel.Messages))
            Refresh();
    }

    private void Refresh()
    {
        if (_model is null) return;
        var rows = _model.ArchivedConversations.Select(c => new ConversationRowViewModel
        {
            PeerIP      = c.PeerIP,
            PeerName    = c.PeerName,
            PhotoB64    = c.PhotoB64,
            LastMessage = c.LastMessage,
            Timestamp   = Theme.FormatTimestamp(c.LastTimestamp),
            UnreadCount = c.UnreadCount,
            IsTyping    = c.IsTyping,
            IsOnline    = c.IsOnline,
            IsArchived  = true,
        }).ToList();
        ArchivedList.ItemsSource = rows;
        EmptyState.Visibility    = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (sender is Button btn && btn.Tag is string ip)
        {
            _model.UnarchiveConversation(ip);
            _model.SelectedPeerIP = ip;
            ConversationOpened?.Invoke(ip);
        }
    }

    private void UnarchiveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (sender is Button btn && btn.Tag is string ip)
            _model.UnarchiveConversation(ip);
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
}
