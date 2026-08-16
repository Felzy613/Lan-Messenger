using LanMessenger.Core.Persistence;
using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.ComponentModel;
using Windows.UI;

namespace LanMessenger.UI.Sidebar;

// ViewModel for a single contact row.
// Properties are mutable so Refresh() can update the in-memory list and the
// code-built UI can read fresh values on each ApplyFilter() rebuild.
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

    // Derived display properties — used when building rows in code.
    public string StatusText => _isOnline ? "Online" : "Offline";

    public SolidColorBrush StatusBrush => _isOnline
        ? new SolidColorBrush(Color.FromArgb(255, 37, 211, 102))
        : new SolidColorBrush(Color.FromArgb(255, 134, 134, 134));

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

    // Raised for actions that switch the hosting ContactsDialog to a different
    // state (Find Contacts / Edit / Delete) — see ContactsDialog.cs. This page
    // is one of several content states swapped inside a single persistent
    // dialog, so these are just "please switch state" signals, not requests
    // to open another dialog.
    public event Action? SearchLanRequested;
    public event Action<string>? EditContactRequested;
    public event Action<string>? DeleteContactRequested;

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

    // Rebuilds _allRows from the saved contacts list and triggers a filter pass.
    private void Refresh()
    {
        var onlineIPs = _model?.Peers.Values
                               .Where(p => p.IsOnline)
                               .Select(p => p.IP)
                               .ToHashSet() ?? [];

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

    // Rebuilds the ListView contents — one flat "Contacts" section, matching
    // macOS (no Online/Offline split; each row still shows its own status).
    private void ApplyFilter()
    {
        var query    = SearchBox?.Text.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(r =>
                  r.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                  r.LastIP.Contains(query,   StringComparison.OrdinalIgnoreCase)).ToList();

        var sorted = filtered.OrderBy(r => r.Username, StringComparer.OrdinalIgnoreCase).ToList();

        ContactsList.Items.Clear();

        if (sorted.Count > 0)
        {
            ContactsList.Items.Add(MakeSectionHeader("Contacts"));
            foreach (var vm in sorted) ContactsList.Items.Add(MakeContactRow(vm));
        }

        var hasResults = sorted.Count > 0;
        EmptyState.Visibility    = hasResults ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text          = string.IsNullOrEmpty(query) ? "No saved contacts" : "No matches";
        EmptySubtitle.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Row / section builders ───────────────────────────────────────────────

    private static UIElement MakeSectionHeader(string text)
    {
        return new TextBlock
        {
            Text     = text,
            Style    = TryGetStyle("CaptionTextBlockStyle"),
            Foreground = TryGetBrush("TextFillColorSecondaryBrush"),
            Margin   = new Thickness(16, 8, 16, 4),
        };
    }

    private UIElement MakeContactRow(ContactRowViewModel vm)
    {
        // Avatar with online dot overlay.
        var avatar = new AvatarControl
        {
            Width   = 44,
            Height  = 44,
            NameText = vm.Username,
            PhotoB64 = vm.PhotoB64,
        };
        var dot = new Ellipse
        {
            Width               = 11,
            Height              = 11,
            Fill                = new SolidColorBrush(Color.FromArgb(255, 37, 211, 102)),
            Stroke              = new SolidColorBrush(Microsoft.UI.Colors.White),
            StrokeThickness     = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Visibility          = vm.IsOnline ? Visibility.Visible : Visibility.Collapsed,
        };
        var avatarGrid = new Grid();
        avatarGrid.Children.Add(avatar);
        avatarGrid.Children.Add(dot);

        // Name + status.
        var nameBlock = new TextBlock
        {
            Text  = vm.Username,
            Style = TryGetStyle("BodyStrongTextBlockStyle"),
        };
        var statusBlock = new TextBlock
        {
            Text       = vm.StatusText,
            FontSize   = 11,
            Foreground = vm.StatusBrush,
        };
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(nameBlock);
        info.Children.Add(statusBlock);

        // Three-dot options menu.
        var editItem = new MenuFlyoutItem
        {
            Text = "Edit...",
            Tag  = vm.PublicKeyB64,
            Icon = new FontIcon { Glyph = "\uE70F" },
        };
        editItem.Click += EditBtn_Click;

        var deleteItem = new MenuFlyoutItem
        {
            Text = "Remove",
            Tag  = vm.PublicKeyB64,
            Icon = new FontIcon { Glyph = "\uE74D" },
        };
        deleteItem.Click += DeleteBtn_Click;

        var flyout = new MenuFlyout();
        flyout.Items.Add(editItem);
        flyout.Items.Add(deleteItem);

        var menuBtn = new Button
        {
            Width           = 32,
            Height          = 32,
            Padding         = new Thickness(0),
            Background      = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Tag             = vm.PublicKeyB64,
            Flyout          = flyout,
            Content         = new FontIcon
            {
                Glyph      = "\uE712",
                FontSize   = 14,
            },
        };
        ToolTipService.SetToolTip(menuBtn, "Contact options");

        // Assemble the row.
        var row = new Grid
        {
            ColumnSpacing = 12,
            Padding       = new Thickness(16, 6, 8, 6),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(avatarGrid, 0);
        Grid.SetColumn(info,       1);
        Grid.SetColumn(menuBtn,    2);
        row.Children.Add(avatarGrid);
        row.Children.Add(info);
        row.Children.Add(menuBtn);
        return row;
    }

    // ── Theme-resource helpers ───────────────────────────────────────────────

    private static Style? TryGetStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Style : null;

    private static Brush? TryGetBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;

    // ── Event handlers ───────────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearSearchBtn.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text            = "";
        ClearSearchBtn.Visibility = Visibility.Collapsed;
        ApplyFilter();
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string keyB64) return;
        DeleteContactRequested?.Invoke(keyB64);
    }

    private void EditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string keyB64) return;
        EditContactRequested?.Invoke(keyB64);
    }

    private void AddFromPeers_Click(object sender, RoutedEventArgs e) => SearchLanRequested?.Invoke();
}
