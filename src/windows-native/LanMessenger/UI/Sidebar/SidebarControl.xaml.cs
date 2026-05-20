using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace LanMessenger.UI.Sidebar;

// ViewModel for a single conversation row.
// Implements INPC so we can update individual rows without rebuilding the whole list.
public sealed class ConversationRowViewModel : INotifyPropertyChanged
{
    public string PeerIP { get; init; } = "";

    private string _peerName = "";
    public string PeerName
    {
        get => _peerName;
        set { if (_peerName != value) { _peerName = value; Notify(nameof(PeerName)); } }
    }

    private string? _photoB64;
    public string? PhotoB64
    {
        get => _photoB64;
        set { if (_photoB64 != value) { _photoB64 = value; Notify(nameof(PhotoB64)); } }
    }

    private string _lastMessage = "";
    public string LastMessage
    {
        get => _lastMessage;
        set { if (_lastMessage != value) { _lastMessage = value; Notify(nameof(LastMessage)); } }
    }

    private string _timestamp = "";
    public string Timestamp
    {
        get => _timestamp;
        set { if (_timestamp != value) { _timestamp = value; Notify(nameof(Timestamp)); } }
    }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set { if (_unreadCount != value) { _unreadCount = value; Notify(nameof(UnreadCount)); } }
    }

    private bool _isTyping;
    public bool IsTyping
    {
        get => _isTyping;
        set { if (_isTyping != value) { _isTyping = value; Notify(nameof(IsTyping)); } }
    }

    private bool _isOnline;
    public bool IsOnline
    {
        get => _isOnline;
        set { if (_isOnline != value) { _isOnline = value; Notify(nameof(IsOnline)); } }
    }

    private bool _isArchived;
    public bool IsArchived
    {
        get => _isArchived;
        set { if (_isArchived != value) { _isArchived = value; Notify(nameof(IsArchived)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class SidebarControl : UserControl
{
    public event Action<string>? ConversationSelected;
    public event Action?         SettingsRequested;
    public event Action?         ArchivedRequested;

    // Keep a single ObservableCollection bound to the ListView — never reassign ItemsSource,
    // and mutate this collection in place so item containers (and selection) survive refreshes.
    private readonly ObservableCollection<ConversationRowViewModel> _rows = [];

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
                Refresh();
            }
        }
    }

    public SidebarControl()
    {
        InitializeComponent();
        ConversationList.ItemsSource = _rows;
        ConversationList.ContainerContentChanging += OnContainerContentChanging;
    }

    private void OnModelPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        // AppModel.RefreshConversations() rebuilds Conversations whenever Peers
        // / Messages / TypingStates change in a way that matters to the sidebar
        // (new message, typing, online/offline). Listening to those properties
        // separately is wasted work — every receipt and intermediate status
        // update would otherwise trigger a full sidebar refresh and decode
        // every visible avatar. ArchivedConversations is tracked separately
        // because the archive footer count must update independently.
        if (e.PropertyName is nameof(AppModel.Conversations) or nameof(AppModel.ArchivedConversations))
            Refresh();
    }

    private void Refresh()
    {
        if (_model is null) return;

        // Merge: keep existing rows where possible (so selection survives), update fields in-place,
        // remove rows no longer present, and add new ones in the correct order.
        var target = _model.Conversations;
        var byIP   = _rows.ToDictionary(r => r.PeerIP, r => r);

        // Pass 1: ensure each target item has a row.
        for (var i = 0; i < target.Count; i++)
        {
            var c = target[i];
            var online = c.IsOnline;
            var preview = c.IsTyping ? $"{c.TypingSender} is typing…" : c.LastMessage;
            if (byIP.TryGetValue(c.PeerIP, out var existing))
            {
                existing.PeerName    = c.PeerName;
                existing.PhotoB64    = c.PhotoB64;
                existing.LastMessage = preview;
                existing.Timestamp   = Theme.FormatTimestamp(c.LastTimestamp);
                existing.UnreadCount = c.UnreadCount;
                existing.IsTyping    = c.IsTyping;
                existing.IsOnline    = online;
                existing.IsArchived  = c.IsArchived;

                var currentIdx = _rows.IndexOf(existing);
                if (currentIdx != i && currentIdx >= 0)
                    _rows.Move(currentIdx, i);
            }
            else
            {
                _rows.Insert(i, new ConversationRowViewModel
                {
                    PeerIP      = c.PeerIP,
                    PeerName    = c.PeerName,
                    PhotoB64    = c.PhotoB64,
                    LastMessage = preview,
                    Timestamp   = Theme.FormatTimestamp(c.LastTimestamp),
                    UnreadCount = c.UnreadCount,
                    IsTyping    = c.IsTyping,
                    IsOnline    = online,
                    IsArchived  = c.IsArchived,
                });
            }
        }

        // Pass 2: drop any leftover rows whose IP isn't in target.
        var keepIPs = target.Select(c => c.PeerIP).ToHashSet();
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (!keepIPs.Contains(_rows[i].PeerIP)) _rows.RemoveAt(i);
        }

        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Archived footer
        if (_model.ArchivedConversations.Count > 0)
        {
            ArchivedSection.Visibility = Visibility.Visible;
            var count = _model.ArchivedConversations.Count;
            ArchivedSubtitle.Text = $"{count} conversation{(count == 1 ? "" : "s")}";
        }
        else
        {
            ArchivedSection.Visibility = Visibility.Collapsed;
        }

        // Restore selection without re-firing SelectionChanged.
        if (_model.SelectedPeerIP is not null)
        {
            var idx = -1;
            for (var i = 0; i < _rows.Count; i++)
                if (_rows[i].PeerIP == _model.SelectedPeerIP) { idx = i; break; }
            if (idx >= 0 && ConversationList.SelectedIndex != idx)
                ConversationList.SelectedIndex = idx;
        }
    }

    // Inject Model into each row template so the 3-dot menu can call archive/delete.
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is ConversationRowControl row)
            row.Model = _model;
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is ConversationRowViewModel row)
            ConversationSelected?.Invoke(row.PeerIP);
    }

    private void ArchivedSection_Click(object sender, RoutedEventArgs e) =>
        ArchivedRequested?.Invoke();
}
