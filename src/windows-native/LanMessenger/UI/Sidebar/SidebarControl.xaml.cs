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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class SidebarControl : UserControl
{
    public event Action<string>? ConversationSelected;
    public event Action?         SettingsRequested;

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
    }

    private void OnModelPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Conversations) or nameof(AppModel.Peers))
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
                // Mutate in place.
                existing.PeerName    = c.PeerName;
                existing.LastMessage = preview;
                existing.Timestamp   = Theme.FormatTimestamp(c.LastTimestamp);
                existing.UnreadCount = c.UnreadCount;
                existing.IsTyping    = c.IsTyping;
                existing.IsOnline    = online;

                // Move row to the correct index if it drifted.
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
                    LastMessage = preview,
                    Timestamp   = Theme.FormatTimestamp(c.LastTimestamp),
                    UnreadCount = c.UnreadCount,
                    IsTyping    = c.IsTyping,
                    IsOnline    = online,
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

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is ConversationRowViewModel row)
            ConversationSelected?.Invoke(row.PeerIP);
    }
}
