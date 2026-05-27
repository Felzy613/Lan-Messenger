using LanMessenger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using Windows.Media.Core;

namespace LanMessenger.UI.Chat;

// WhatsApp-style full-screen media viewer.
//
// Design:
//   • Dark (#1F2C34) top bar: × close, filename + date/time, "show in folder" icon.
//   • Pure-black (#000) media area: image letter-boxed at full resolution, or
//     video with built-in transport controls.
//   • Dark (#1F2C34) bottom bar: file-type · file-size metadata.
//   • All built-in ContentDialog chrome (title bar, button row, border) is
//     suppressed via theme-resource overrides in the XAML.
//   • ContentHost.MinWidth/MinHeight are matched to XamlRoot.Size in OnOpened
//     so the viewer fills the hosting window without hard-coded pixel values.
//   • Escape closes the dialog (ContentDialog handles this natively when no
//     CloseButtonText is set, by calling Hide() via its internal OnKeyDown).
//   • The media source is detached in OnClosed so the OS releases decoder
//     and file handles promptly; this also prevents background audio.
public sealed partial class MediaPreviewDialog : ContentDialog
{
    private readonly string    _path;
    private readonly MediaKind _kind;

    public MediaPreviewDialog(string path, MediaKind kind, string filename)
    {
        InitializeComponent();
        _path = path;
        _kind = kind;

        FilenameText.Text = filename;
        DateText.Text     = FormatDateForHeader(path);
        FileInfoText.Text = BuildFileInfo(path, kind);

        try
        {
            if (kind == MediaKind.Image)
            {
                // BitmapImage with no DecodePixelWidth → full-resolution decode;
                // Stretch="Uniform" in the XAML letter-boxes it within the viewer.
                var uri = new Uri(path);
                PreviewImage.Source     = new BitmapImage(uri);
                PreviewImage.Visibility = Visibility.Visible;
            }
            else if (kind == MediaKind.Video)
            {
                PreviewPlayer.Source     = MediaSource.CreateFromUri(new Uri(path));
                PreviewPlayer.Visibility = Visibility.Visible;
            }
            else
            {
                ShowError("Cannot preview this file type.");
            }
        }
        catch (Exception ex)
        {
            LanLogger.Error("MediaPreview", $"failed to load preview for {path}", ex);
            ShowError("Could not load preview.");
        }

        Closed += OnClosed;
    }

    // ── Sizing ───────────────────────────────────────────────────────────────

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // Expand the content grid to fill the hosting window.  The dialog's
        // MaxWidth/MaxHeight are set to 9999 in XAML so MinWidth/MinHeight
        // here drives the actual card size without clipping.
        if (XamlRoot is { Size: var size } && size.Width > 0)
        {
            ContentHost.MinWidth  = size.Width;
            ContentHost.MinHeight = size.Height;
        }
    }

    // ── Toolbar actions ──────────────────────────────────────────────────────

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

    private async void ShowInFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var error = await FileReveal.RevealAsync(_path);
        if (error is not null)
        {
            // Surface the error in the bottom bar so the media remains visible.
            FileInfoText.Text = $"⚠ {error}";
        }
    }

    // ── Media cleanup ────────────────────────────────────────────────────────

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        try
        {
            if (PreviewPlayer.MediaPlayer is not null)
                PreviewPlayer.MediaPlayer.Pause();
            PreviewPlayer.Source = null;
            PreviewImage.Source  = null;
        }
        catch { /* shutdown is best-effort */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ShowError(string message)
    {
        PreviewImage.Visibility  = Visibility.Collapsed;
        PreviewPlayer.Visibility = Visibility.Collapsed;
        ErrorText.Text           = message;
        ErrorPanel.Visibility    = Visibility.Visible;
    }

    /// <summary>
    /// Returns a human-readable date/time string for the top-bar subtitle,
    /// derived from the file's last-write time.
    /// </summary>
    private static string FormatDateForHeader(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "";

            var dt    = info.LastWriteTime;
            var today = DateTime.Today;
            if (dt.Date == today)               return $"Today at {dt:h:mm tt}";
            if (dt.Date == today.AddDays(-1))   return $"Yesterday at {dt:h:mm tt}";
            return dt.ToString("MMM d, yyyy · h:mm tt");
        }
        catch { return ""; }
    }

    /// <summary>
    /// Returns a "<EXT> Image/Video/File  ·  <size>" string for the bottom bar.
    /// </summary>
    private static string BuildFileInfo(string path, MediaKind kind)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "";

            var ext   = info.Extension.TrimStart('.').ToUpperInvariant();
            var label = kind switch
            {
                MediaKind.Image => $"{ext} Image",
                MediaKind.Video => $"{ext} Video",
                _               => $"{ext} File",
            };
            return $"{label}  ·  {FormatFileSize(info.Length)}";
        }
        catch { return ""; }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1_024)           return $"{bytes} B";
        if (bytes < 1_048_576)       return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes / 1_048_576.0:F1} MB";
    }
}
