using LanMessenger.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using Windows.UI;

namespace LanMessenger.UI.Sidebar;

public sealed class ContactRowViewModel : INotifyPropertyChanged
{
    public string PublicKeyB64 { get; init; } = "";

    private string _username = "";
    public string Username
    {
        get => _username;
        set { if (_username != value) { _username = value; Notify(nameof(Username)); } }
    }

    private string _lastIP = "";
    public string LastIP
    {
        get => _lastIP;
        set
        {
            if (_lastIP != value)
            {
                _lastIP = value;
                Notify(nameof(LastIP));
                Notify(nameof(StatusText));
            }
        }
    }

    private bool _isOnline;
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (_isOnline != value)
            {
                _isOnline = value;
                Notify(nameof(IsOnline));
                Notify(nameof(IsOnlineVisibility));
                Notify(nameof(StatusText));
                Notify(nameof(StatusBrush));
            }
        }
    }

    private string? _photoB64;
    public string? PhotoB64
    {
        get => _photoB64;
        set { if (_photoB64 != value) { _photoB64 = value; Notify(nameof(PhotoB64)); } }
    }

    // Derived display properties used by x:Bind
    public Visibility IsOnlineVisibility => _isOnline ? Visibility.Visible : Visibility.Collapsed;
    public string     StatusText  => _isOnline ? "Online" : (_lastIP.Length > 0 ? _lastIP : "—");
    public SolidColorBrush StatusBrush => _isOnline
        ? new SolidColorBrush(Color.FromArgb(255, 37, 211, 102))   // #25D366
        : new SolidColorBrush(Color.FromArgb(255, 134, 134, 134)); // secondary

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class ContactsPage : Page
{
    private AppModel? _model;
    public AppModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = value;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
        }
    }

    private List<ContactRowViewModel> _allRows = [];

    public ContactsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void OnModelChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Conversations) or nameof(AppModel.Peers))
            DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        var onlineIPs = _model?.Peers.Values.Select(p => p.IP).ToHashSet() ?? [];
        _allRows = ConfigStore.Shared.Config.Contacts.Select(c => new ContactRowViewModel
        {
            PublicKeyB64 = c.PublicKeyB64,
            Username     = c.Username,
            LastIP       = c.LastIP,
            PhotoB64     = c.PhotoB64,
            IsOnline     = onlineIPs.Contains(c.LastIP),
        }).ToList();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox?.Text.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(r =>
                r.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.LastIP.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        ContactsList.ItemsSource = filtered;
        EmptyState.Visibility    = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text          = string.IsNullOrEmpty(query) ? "No saved contacts" : "No matches";
        EmptySubtitle.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearSearchBtn.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ClearSearchBtn.Visibility = Visibility.Collapsed;
        ApplyFilter();
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string keyB64) return;
        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == keyB64);
        var dialog = new ContentDialog
        {
            Title             = "Remove contact?",
            Content           = $"Remove {contact?.Username ?? "contact"} and delete the conversation?",
            PrimaryButtonText = "Remove",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _model?.DeleteContact(keyB64);
            Refresh();
        }
    }

    private async void EditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (sender is not FrameworkElement el || el.Tag is not string keyB64) return;
        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == keyB64);
        if (contact is null) return;
        var editor = new ContactEditorDialog(contact) { XamlRoot = XamlRoot };
        var result = await editor.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _model.UpdateContact(keyB64, editor.NameValue, editor.PhotoB64Value);
            Refresh();
        }
    }

    private async void AddFromPeers_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var picker = new PeerPickerDialog(_model) { XamlRoot = XamlRoot };
        await picker.ShowAsync();
        Refresh();
    }
}
