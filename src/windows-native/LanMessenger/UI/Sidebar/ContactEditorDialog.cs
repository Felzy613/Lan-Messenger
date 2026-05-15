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

// Lets the user pick currently-discovered peers and add them as contacts.
public sealed class PeerPickerDialog : ContentDialog
{
    private readonly AppModel _model;
    private readonly ListView _list;

    public PeerPickerDialog(AppModel model)
    {
        _model = model;
        Title = "Add Contact from Nearby Peers";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;

        _list = new ListView { SelectionMode = ListViewSelectionMode.None, MinWidth = 360, MinHeight = 280 };
        Refresh();
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.Peers)) Refresh();
        };

        Content = _list;
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
            var avatar = new AvatarControl { Width = 36, Height = 36, NameText = peer.Username };
            var name   = new TextBlock { Text = peer.Username, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            var ip     = new TextBlock { Text = peer.IP, Opacity = 0.6, FontSize = 11 };
            var info   = new StackPanel { Spacing = 2 };
            info.Children.Add(name);
            info.Children.Add(ip);
            var addBtn = new Button { Content = "Add" };
            var capturedPeer = peer;
            addBtn.Click += (_, _) =>
            {
                _model.AddContact(capturedPeer.PublicKeyB64, capturedPeer.Username, capturedPeer.IP);
                Refresh();
            };

            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(info, 1);
            Grid.SetColumn(addBtn, 2);
            row.Children.Add(avatar);
            row.Children.Add(info);
            row.Children.Add(addBtn);
            _list.Items.Add(row);
        }
    }
}
