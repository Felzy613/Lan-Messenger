using LanMessenger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using Windows.Media.Core;

namespace LanMessenger.UI.Chat;

// Full-screen-style media viewer used when the user taps a photo or video bubble.
//
// Design:
//   • Image previews use BitmapImage with no DecodePixelWidth so the user sees
//     full resolution (zoom is provided by the user widening the dialog).
//   • Video previews use MediaPlayerElement with built-in transport controls.
//     The element is paused and the source is detached when the dialog closes
//     to free the decoder and avoid background audio.
//   • Loads use Uri.FromFile() and never block the UI thread on disk I/O — the
//     image decode is performed asynchronously by the XAML pipeline.
public sealed partial class MediaPreviewDialog : ContentDialog
{
    private readonly string _path;
    private readonly MediaKind _kind;

    public MediaPreviewDialog(string path, MediaKind kind, string filename)
    {
        InitializeComponent();
        _path = path;
        _kind = kind;
        Title = filename;

        try
        {
            if (kind == MediaKind.Image)
            {
                var uri = new Uri(path);
                PreviewImage.Source = new BitmapImage(uri);
                PreviewImage.Visibility = Visibility.Visible;
            }
            else if (kind == MediaKind.Video)
            {
                PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
                PreviewPlayer.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorText.Text = "Cannot preview this file.";
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            LanLogger.Error("MediaPreview", $"failed to load preview for {path}", ex);
            ErrorText.Text = "Could not load preview.";
            ErrorText.Visibility = Visibility.Visible;
        }

        // "Show in folder" button.
        PrimaryButtonClick += OnShowInFolder;
        Closed += OnClosed;
    }

    private async void OnShowInFolder(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open while the reveal runs so a transient failure can
        // be reported back to the user.  Deferral keeps Closed from firing.
        var deferral = args.GetDeferral();
        try
        {
            var error = await FileReveal.RevealAsync(_path);
            if (error is not null)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                args.Cancel = true;   // keep dialog open so user sees the error
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        // Detach the media so the OS releases the decoder + file handle promptly.
        // Without this, the file stays open in the player even though the dialog
        // is gone, which prevents tools like the test suite from re-reading it.
        try
        {
            if (PreviewPlayer.MediaPlayer is not null)
            {
                PreviewPlayer.MediaPlayer.Pause();
            }
            PreviewPlayer.Source = null;
            PreviewImage.Source = null;
        }
        catch { /* shutdown is best-effort */ }
    }
}
