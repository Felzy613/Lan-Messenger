using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Storage.Streams;

namespace LanMessenger.UI;

public sealed partial class AvatarControl : UserControl
{
    public static readonly DependencyProperty NameTextProperty =
        DependencyProperty.Register(nameof(NameText), typeof(string), typeof(AvatarControl),
            new PropertyMetadata("", OnVisualChanged));

    public static readonly DependencyProperty PhotoB64Property =
        DependencyProperty.Register(nameof(PhotoB64), typeof(string), typeof(AvatarControl),
            new PropertyMetadata(null, OnVisualChanged));

    public string NameText
    {
        get => (string)GetValue(NameTextProperty);
        set => SetValue(NameTextProperty, value);
    }

    public string? PhotoB64
    {
        get => (string?)GetValue(PhotoB64Property);
        set => SetValue(PhotoB64Property, value);
    }

    public AvatarControl() => InitializeComponent();

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AvatarControl ctrl) ctrl.Refresh();
    }

    // Cached so we skip the expensive base64 decode + BitmapImage decode on
    // re-renders that don't actually change the photo. SidebarControl.Refresh
    // fires on every conversation event (new message, status update, typing)
    // and would otherwise re-decode every visible avatar every time.
    private string _lastName = "";
    private string? _lastPhotoB64;

    private async void Refresh()
    {
        var name  = NameText ?? "";
        var photo = PhotoB64;

        if (name != _lastName)
        {
            _lastName = name;
            BackgroundEllipse.Fill = Theme.AvatarBrush(name);
            InitialsText.Text      = Theme.Initials(name);
        }

        if (photo == _lastPhotoB64) return;
        _lastPhotoB64 = photo;

        if (string.IsNullOrEmpty(photo))
        {
            PhotoEllipse.Visibility      = Visibility.Collapsed;
            InitialsText.Visibility      = Visibility.Visible;
            BackgroundEllipse.Visibility = Visibility.Visible;
            PhotoBrush.ImageSource       = null;
            return;
        }
        try
        {
            var bytes = System.Convert.FromBase64String(photo);
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            PhotoBrush.ImageSource       = bitmap;
            PhotoEllipse.Visibility      = Visibility.Visible;
            InitialsText.Visibility      = Visibility.Collapsed;
            BackgroundEllipse.Visibility = Visibility.Collapsed;
        }
        catch
        {
            PhotoEllipse.Visibility      = Visibility.Collapsed;
            InitialsText.Visibility      = Visibility.Visible;
            BackgroundEllipse.Visibility = Visibility.Visible;
        }
    }
}
