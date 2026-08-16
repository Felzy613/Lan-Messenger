using LanMessenger.Core.Persistence;
using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI.Sidebar;

// "New message" picker — shows the user's saved contacts and lets them pick one to
// open a thread with. Returns ContentDialogResult.Primary if the user hits "Add Contact"
// so the host can swap to the contacts dialog.
//
// This is a standalone flow (starting a conversation), not part of the Contacts
// editing chain — see ContactsDialog.cs for the Contacts/Find Contacts/Name/Edit/
// Delete flow, which is a single persistent ContentDialog for the reasons
// documented there.
public sealed class NewMessageDialog : ContentDialog
{
    private readonly AppModel _model;
    private readonly TextBox _searchBox;
    private readonly ListView _list;

    public NewMessageDialog(AppModel model)
    {
        _model = model;
        Title             = "New Message";
        PrimaryButtonText = "Add Contact";
        CloseButtonText   = "Cancel";
        DefaultButton     = ContentDialogButton.Close;

        _searchBox = new TextBox
        {
            PlaceholderText = "Search contacts",
            MinWidth = 360,
        };
        _searchBox.TextChanged += (_, _) => Refresh();

        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            MinWidth = 360,
            MinHeight = 320,
        };

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(_searchBox);
        root.Children.Add(_list);
        Content = root;

        Refresh();
        _model.PropertyChanged += OnModelChanged;
        Closed += (_, _) => _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.Peers) ||
            e.PropertyName == nameof(AppModel.Conversations))
            Refresh();
    }

    private void Refresh()
    {
        var query = _searchBox.Text.Trim();
        var onlineKeys = _model.Peers.Values.Where(p => p.IsOnline).Select(p => p.PublicKeyB64).ToHashSet();
        var rows = ConfigStore.Shared.Config.Contacts
            .Where(c => string.IsNullOrEmpty(query) ||
                c.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.LastIP.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _list.Items.Clear();
        if (rows.Count == 0)
        {
            _list.Items.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(query)
                    ? "No saved contacts yet. Click \"Add Contact\" below to find peers on your LAN."
                    : "No matches.",
                Margin = new Thickness(8),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var contact in rows)
        {
            var captured = contact;
            var isOnline = onlineKeys.Contains(contact.PublicKeyB64);

            var avatar = new AvatarControl { Width = 40, Height = 40, NameText = contact.Username, PhotoB64 = contact.PhotoB64 };
            var name   = new TextBlock { Text = contact.Username, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            var status = new TextBlock
            {
                Text = isOnline ? "Online" : "Offline",
                Opacity = 0.7,
                FontSize = 11,
            };
            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(name);
            info.Children.Add(status);

            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(info, 1);
            row.Children.Add(avatar);
            row.Children.Add(info);
            row.Tapped += (_, _) =>
            {
                _model.StartConversation(captured.PublicKeyB64);
                Hide();
            };
            _list.Items.Add(row);
        }
    }
}
