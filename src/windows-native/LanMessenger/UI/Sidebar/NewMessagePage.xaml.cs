using LanMessenger.Core.Persistence;
using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.ComponentModel;
using Windows.UI;

namespace LanMessenger.UI.Sidebar;

// "New message" picker — shows saved contacts and lets the user pick one to
// open (or resume) a thread. Visually this is ContactsPage's search bar and
// row style reused verbatim (see ContactsPage.xaml.cs), because the two
// sheets are the same shape as macOS's ContactsView / NewMessageView. Hosted
// as ContentDialog content by NewMessageDialog, exactly like ContactsPage is
// hosted by ContactsDialog.
public sealed partial class NewMessagePage : Page
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

    /// Raised when the user taps a contact row — the hosting dialog closes.
    public event Action<string>? ContactSelected;
    /// Raised from the empty state's "Add a contact" button.
    public event Action? AddContactRequested;

    public NewMessagePage()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyFilter();
    }

    private void OnModelChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Peers) or nameof(AppModel.Conversations))
            DispatcherQueue.TryEnqueue(ApplyFilter);
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var onlineKeys = _model?.Peers.Values.Where(p => p.IsOnline)
            .Select(p => p.PublicKeyB64).ToHashSet() ?? [];

        var rows = ConfigStore.Shared.Config.Contacts
            .Where(c => string.IsNullOrEmpty(query) ||
                c.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.LastIP.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ContactsList.Items.Clear();
        foreach (var contact in rows)
            ContactsList.Items.Add(MakeContactRow(contact, onlineKeys.Contains(contact.PublicKeyB64)));

        var hasResults = rows.Count > 0;
        ContactsList.Visibility = hasResults ? Visibility.Visible   : Visibility.Collapsed;
        EmptyState.Visibility   = hasResults ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text         = string.IsNullOrEmpty(query) ? "No saved contacts" : "No matches";
        AddContactBtn.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement MakeContactRow(ContactConfig contact, bool isOnline)
    {
        var avatar = new AvatarControl
        {
            Width = 44, Height = 44,
            NameText = contact.Username,
            PhotoB64 = contact.PhotoB64,
        };
        var dot = new Ellipse
        {
            Width               = 11,
            Height              = 11,
            Fill                = Theme.OnlineDotBrush,
            Stroke              = new SolidColorBrush(GlassTokens.Pick(GlassTokens.PresenceRingLight, GlassTokens.PresenceRingDark)),
            StrokeThickness     = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Visibility          = isOnline ? Visibility.Visible : Visibility.Collapsed,
        };
        var avatarGrid = new Grid();
        avatarGrid.Children.Add(avatar);
        avatarGrid.Children.Add(dot);

        var nameBlock = new TextBlock
        {
            Text         = contact.Username,
            Style        = TryGetStyle("TokenTypeNameStyle"),
            Foreground   = Theme.InkBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Matches macOS's NewMessageView contact row: "Online" or the saved
        // IP, both in secondary (not accent-colored) text — unlike the
        // Contacts sheet, which colors "Online" green.
        var statusBlock = new TextBlock
        {
            Text       = isOnline ? "Online" : (string.IsNullOrEmpty(contact.LastIP) ? "—" : contact.LastIP),
            Style      = TryGetStyle("TokenTypeCaptionStyle"),
            Foreground = Theme.InkSecondaryBrush,
        };
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(nameBlock);
        info.Children.Add(statusBlock);

        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 6, 8, 6), Tag = contact.PublicKeyB64 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(avatarGrid, 0);
        Grid.SetColumn(info,       1);
        row.Children.Add(avatarGrid);
        row.Children.Add(info);
        return row;
    }

    private static Style? TryGetStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Style : null;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    // An item click rather than a Tapped handler on the row: the ListView
    // then gives the row its pressed state and answers Enter and Space.
    private void ContactsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement { Tag: string publicKeyB64 })
            ContactSelected?.Invoke(publicKeyB64);
    }

    private void AddContact_Click(object sender, RoutedEventArgs e) => AddContactRequested?.Invoke();
}
