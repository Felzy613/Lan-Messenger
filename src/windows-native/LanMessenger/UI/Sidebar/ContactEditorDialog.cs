using LanMessenger.Core.Persistence;
using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace LanMessenger.UI.Sidebar;

// A code-only ContentDialog so we don't have to wire another .xaml.cs partial.
// Lets the user change the contact's display name and avatar photo.
public sealed class ContactEditorDialog : ContentDialog
{
    private readonly ContactConfig _contact;
    private readonly TextBox _nameBox;
    private readonly AvatarControl _avatar;
    private string? _currentPhotoB64;

    public string NameValue => _nameBox.Text.Trim();
    public string? PhotoB64Value => _currentPhotoB64;

    public ContactEditorDialog(ContactConfig contact)
    {
        _contact = contact;
        _currentPhotoB64 = contact.PhotoB64;

        Title = "Edit Contact";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _avatar = new AvatarControl
        {
            Width = 88,
            Height = 88,
            NameText = contact.Username,
            PhotoB64 = contact.PhotoB64,
        };
        _nameBox = new TextBox
        {
            Text = contact.Username,
            Header = "Display name",
            PlaceholderText = "Contact name",
            MinWidth = 260,
        };
        _nameBox.TextChanged += (_, _) => _avatar.NameText = _nameBox.Text;

        var choose  = new Button { Content = "Choose Photo…" };
        choose.Click += async (_, _) => await PickPhotoAsync();
        var remove  = new Button { Content = "Remove Photo" };
        remove.Click += (_, _) =>
        {
            _currentPhotoB64 = null;
            _avatar.PhotoB64 = null;
        };

        var photoButtons = new StackPanel { Spacing = 8, Orientation = Orientation.Vertical };
        photoButtons.Children.Add(choose);
        photoButtons.Children.Add(remove);

        var avatarRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        avatarRow.Children.Add(_avatar);
        avatarRow.Children.Add(photoButtons);

        var deviceIdLabel = new TextBlock
        {
            Text = $"Device ID: {(contact.PublicKeyB64.Length > 16 ? contact.PublicKeyB64[..16] + "…" : contact.PublicKeyB64)}",
            FontSize = 11,
            Opacity = 0.6,
        };
        var ipLabel = new TextBlock
        {
            Text = $"Last IP: {(string.IsNullOrEmpty(contact.LastIP) ? "—" : contact.LastIP)}",
            FontSize = 11,
            Opacity = 0.6,
        };

        var root = new StackPanel { Spacing = 16, Width = 380 };
        root.Children.Add(avatarRow);
        root.Children.Add(_nameBox);
        root.Children.Add(deviceIdLabel);
        root.Children.Add(ipLabel);
        Content = root;
    }

    private async System.Threading.Tasks.Task PickPhotoAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(((global::LanMessenger.App)Application.Current).MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            using var src = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(src);

            // Re-encode at ~256px on the longest edge so we don't bloat config.json with
            // multi-MB images. JPEG quality stays at the encoder default (~80).
            const double targetMax = 256.0;
            double srcW = decoder.PixelWidth, srcH = decoder.PixelHeight;
            double scale = Math.Min(1.0, targetMax / Math.Max(srcW, srcH));
            uint newW = (uint)Math.Max(1, Math.Round(srcW * scale));
            uint newH = (uint)Math.Max(1, Math.Round(srcH * scale));

            var transform = new BitmapTransform
            {
                ScaledWidth  = newW,
                ScaledHeight = newH,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var pixels = pixelData.DetachPixelData();

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                newW, newH, 96, 96, pixels);
            await encoder.FlushAsync();
            output.Seek(0);
            using var ms = new MemoryStream();
            await output.AsStreamForRead().CopyToAsync(ms);
            _currentPhotoB64 = Convert.ToBase64String(ms.ToArray());
            _avatar.PhotoB64 = _currentPhotoB64;
        }
        catch { /* swallow — keep existing photo */ }
    }
}

// Two-step "Search LAN" flow: pick one-or-more discovered peers, click Save, then
// the dialog walks the user through a per-peer name prompt before persisting.
public sealed class PeerPickerDialog : ContentDialog
{
    private readonly AppModel _model;
    private readonly ListView _list;
    private readonly HashSet<string> _selectedKeys = [];

    // Populated when the user confirms; read by the caller after ShowAsync returns.
    public IReadOnlyList<PeerInfo> SelectedPeers { get; private set; } = [];

    public PeerPickerDialog(AppModel model)
    {
        _model = model;
        Title = "Find Contacts";
        PrimaryButtonText = "Save";
        CloseButtonText   = "Cancel";
        DefaultButton     = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        _list = new ListView { SelectionMode = ListViewSelectionMode.None, MinWidth = 360, MinHeight = 280 };
        Refresh();
        _model.PropertyChanged += OnModelChanged;
        Closed += (_, _) => _model.PropertyChanged -= OnModelChanged;

        Content = _list;
        // Snapshot selection before the dialog closes; the naming flow runs in the
        // caller after ShowAsync() fully completes so no two dialogs overlap.
        PrimaryButtonClick += (_, _) =>
        {
            SelectedPeers = _model.Peers.Values
                .Where(p => _selectedKeys.Contains(p.PublicKeyB64))
                .ToList();
        };
    }

    private void OnModelChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.Peers)) Refresh();
    }

    private void Refresh()
    {
        var savedKeys = ConfigStore.Shared.Config.Contacts.Select(c => c.PublicKeyB64).ToHashSet();
        var rows = _model.Peers.Values
            .Where(p => !savedKeys.Contains(p.PublicKeyB64))
            .OrderBy(p => p.Username)
            .ToList();

        _list.Items.Clear();
        if (rows.Count == 0)
        {
            IsPrimaryButtonEnabled = false;
            _list.Items.Add(new TextBlock
            {
                Text = "No peers found. Make sure other devices are running LAN Messenger on the same network.",
                Margin = new Thickness(8),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var peer in rows)
        {
            var capturedPeer = peer;
            var check = new CheckBox
            {
                IsChecked = _selectedKeys.Contains(peer.PublicKeyB64),
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.Checked   += (_, _) => { _selectedKeys.Add(capturedPeer.PublicKeyB64); UpdateSaveEnabled(); };
            check.Unchecked += (_, _) => { _selectedKeys.Remove(capturedPeer.PublicKeyB64); UpdateSaveEnabled(); };

            var avatar = new AvatarControl { Width = 36, Height = 36, NameText = peer.Username };
            var name   = new TextBlock { Text = peer.Username, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            var ip     = new TextBlock { Text = peer.IP, Opacity = 0.6, FontSize = 11 };
            var info   = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(name);
            info.Children.Add(ip);

            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(check, 0);
            Grid.SetColumn(avatar, 1);
            Grid.SetColumn(info, 2);
            row.Children.Add(check);
            row.Children.Add(avatar);
            row.Children.Add(info);

            // Clicking anywhere on the row toggles the checkbox.
            row.Tapped += (_, _) =>
            {
                check.IsChecked = !(check.IsChecked ?? false);
            };

            _list.Items.Add(row);
        }
        UpdateSaveEnabled();
    }

    private void UpdateSaveEnabled() =>
        IsPrimaryButtonEnabled = _selectedKeys.Count > 0;

}

// Two-button dialog prompting for a custom display name for a freshly-discovered peer.
public sealed class NameContactDialog : ContentDialog
{
    private readonly TextBox _nameBox;
    public string NameValue => _nameBox.Text;

    public NameContactDialog(PeerInfo peer)
    {
        Title             = "Name contact";
        PrimaryButtonText = "Save";
        CloseButtonText   = $"Use \"{peer.Username}\"";
        DefaultButton     = ContentDialogButton.Primary;

        _nameBox = new TextBox
        {
            Text = peer.Username,
            Header = "Display name",
            PlaceholderText = "Contact name",
            MinWidth = 280,
        };

        var avatar = new AvatarControl { Width = 64, Height = 64, NameText = peer.Username };
        _nameBox.TextChanged += (_, _) => avatar.NameText = string.IsNullOrWhiteSpace(_nameBox.Text) ? peer.Username : _nameBox.Text;

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = peer.Username, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        info.Children.Add(new TextBlock { Text = peer.IP, Opacity = 0.6, FontSize = 11 });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        header.Children.Add(avatar);
        header.Children.Add(info);

        var root = new StackPanel { Spacing = 16, Width = 360 };
        root.Children.Add(header);
        root.Children.Add(_nameBox);
        Content = root;
    }
}

// "New message" picker — shows the user's saved contacts and lets them pick one to
// open a thread with. Returns ContentDialogResult.Primary if the user hits "Add Contact"
// so the host can swap to the contacts dialog.
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
                Text = isOnline ? "Online" : (string.IsNullOrEmpty(contact.LastIP) ? "—" : contact.LastIP),
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
