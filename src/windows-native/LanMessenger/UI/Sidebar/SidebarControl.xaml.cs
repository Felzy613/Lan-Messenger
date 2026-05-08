using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI.Sidebar;

// ViewModel for a single conversation row.
public sealed class ConversationRowViewModel
{
    public string    PeerIP        { get; init; } = "";
    public string    PeerName      { get; init; } = "";
    public string    LastMessage   { get; init; } = "";
    public string    Timestamp     { get; init; } = "";
    public int       UnreadCount   { get; init; }
    public bool      IsTyping      { get; init; }
    public bool      IsOnline      { get; init; }
}

public sealed partial class SidebarControl : UserControl
{
    public event Action<string>? ConversationSelected;
    public event Action?         SettingsRequested;

    private AppModel? _model;
    public AppModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            if (_model is not null)
            {
                _model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(AppModel.Conversations) or nameof(AppModel.Peers))
                        Refresh();
                };
                Refresh();
            }
        }
    }

    public SidebarControl() => InitializeComponent();

    private void Refresh()
    {
        if (_model is null) return;

        var items = _model.Conversations.Select(c => new ConversationRowViewModel
        {
            PeerIP      = c.PeerIP,
            PeerName    = c.PeerName,
            LastMessage = c.IsTyping ? $"{c.TypingSender} is typing…" : c.LastMessage,
            Timestamp   = Theme.FormatTimestamp(c.LastTimestamp),
            UnreadCount = c.UnreadCount,
            IsTyping    = c.IsTyping,
            IsOnline    = _model.Peers.Values.FirstOrDefault(p => p.IP == c.PeerIP)?.IsOnline ?? false,
        }).ToList();

        ConversationList.ItemsSource = items;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Re-select if a peer was previously selected
        if (_model.SelectedPeerIP is not null)
        {
            var idx = items.FindIndex(i => i.PeerIP == _model.SelectedPeerIP);
            if (idx >= 0) ConversationList.SelectedIndex = idx;
        }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is ConversationRowViewModel row)
            ConversationSelected?.Invoke(row.PeerIP);
    }
}
