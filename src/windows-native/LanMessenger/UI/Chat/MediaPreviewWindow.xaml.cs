using LanMessenger.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using Windows.Media.Core;

namespace LanMessenger.UI.Chat;

// Standalone centered window for previewing image and video attachments.
// Replaces the old ContentDialog approach so the viewer opens as an independent
// window (same behaviour as the macOS NSPanel) rather than a modal overlay.
//
// Sizing flow:
//   1. Window opens at a default size, centered on the nearest display.
//   2. For images, BitmapImage.ImageOpened fires once decoding finishes;
//      ApplySizeForImage then resizes and re-centers to the image's natural
//      dimensions (capped to the work area with an 80-pt margin).
//   3. For videos, the default size is kept.
//
// Escape closes the window via a KeyboardAccelerator declared in the XAML.
public sealed partial class MediaPreviewWindow : Window
{
    private readonly string    _path;
    private readonly MediaKind _kind;
    private BitmapImage?       _bitmapImage;
    private bool               _sizedOnce;

    public MediaPreviewWindow(string path, MediaKind kind, string filename)
    {
        InitializeComponent();

        _path = path;
        _kind = kind;
        Title = filename;

        // Extend content into the title bar so our dark top bar covers the full
        // window width.  The system close button overlays the reserved column.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TopBarGrid);

        // Style the system caption button to match the dark top bar.
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor         = Colors.Transparent;
        tb.ButtonHoverBackgroundColor    = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        tb.ButtonPressedBackgroundColor  = Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF);
        tb.ButtonForegroundColor         = Colors.White;
        tb.ButtonHoverForegroundColor    = Colors.White;
        tb.ButtonPressedForegroundColor  = Colors.White;

        // Remove minimize / maximize — this is a viewer, not a work window.
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMinimizable = false;
            p.IsMaximizable = false;
        }

        FilenameText.Text = filename;
        DateText.Text     = FormatDateForHeader(path);
        FileInfoText.Text = BuildFileInfo(path, kind);

        try
        {
            if (kind == MediaKind.Image)
            {
                var uri = new Uri(path);
                _bitmapImage              = new BitmapImage(uri);
                _bitmapImage.ImageOpened += OnBitmapImageOpened;
                PreviewImage.Source       = _bitmapImage;
                PreviewImage.Visibility   = Visibility.Visible;
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

        Activated += OnFirstActivated;
        Closed    += OnWindowClosed;
    }

    // ── First-activation sizing ──────────────────────────────────────────────

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_sizedOnce) return;
        _sizedOnce = true;

        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;

        // Align the reserved caption column to the actual system-button width.
        double rightInsetDip = AppWindow.TitleBar.RightInset / scale;
        CaptionButtonsCol.Width = new Microsoft.UI.Xaml.GridLength(rightInsetDip);

        // Apply image dimensions if decoding already finished; otherwise
        // OnBitmapImageOpened will call ApplySizeForImage once it does.
        if (_kind == MediaKind.Image &&
            _bitmapImage is { PixelWidth: > 0, PixelHeight: > 0 } bmp)
        {
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            ApplySizeForImage(bmp.PixelWidth, bmp.PixelHeight, work, scale);
        }
        else
        {
            CenterAt(DefaultPhysicalSize(_kind, scale));
        }
    }

    // ── Image-driven sizing ──────────────────────────────────────────────────

    private void OnBitmapImageOpened(object sender, RoutedEventArgs e)
    {
        if (_bitmapImage is not { PixelWidth: > 0, PixelHeight: > 0 } bmp) return;
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        ApplySizeForImage(bmp.PixelWidth, bmp.PixelHeight, work, scale);
    }

    private void ApplySizeForImage(int pixW, int pixH,
                                   Windows.Graphics.RectInt32 workPhys, double dpiScale)
    {
        // Rows: top-bar (46) + bottom-bar (48) = 94 DIPs of chrome.
        const double BarsH  = 46 + 48;
        const double Margin = 80;

        double workDipW = workPhys.Width  / dpiScale;
        double workDipH = workPhys.Height / dpiScale;

        double maxW = Math.Max(400, workDipW - Margin);
        double maxH = Math.Max(300, workDipH - Margin - BarsH);
        double s    = Math.Min(1.0, Math.Min(maxW / pixW, maxH / pixH));

        double dipW = Math.Max(400,       Math.Round(pixW * s));
        double dipH = Math.Max(200 + BarsH, Math.Round(pixH * s) + BarsH);

        CenterAt(new Windows.Graphics.SizeInt32(
            (int)Math.Round(dipW * dpiScale),
            (int)Math.Round(dipH * dpiScale)));
    }

    private static Windows.Graphics.SizeInt32 DefaultPhysicalSize(MediaKind kind, double dpiScale)
    {
        double dipW = kind == MediaKind.Image ? 800 : 1000;
        double dipH = kind == MediaKind.Image ? 600 : 720;
        return new Windows.Graphics.SizeInt32(
            (int)Math.Round(dipW * dpiScale),
            (int)Math.Round(dipH * dpiScale));
    }

    private void CenterAt(Windows.Graphics.SizeInt32 physSize)
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int x = work.X + Math.Max(0, (work.Width  - physSize.Width)  / 2);
        int y = work.Y + Math.Max(0, (work.Height - physSize.Height) / 2);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, physSize.Width, physSize.Height));
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    // ── Toolbar ──────────────────────────────────────────────────────────────

    private async void ShowInFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var error = await FileReveal.RevealAsync(_path);
        if (error is not null)
            FileInfoText.Text = $"⚠ {error}";
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            if (_bitmapImage is not null)
            {
                _bitmapImage.ImageOpened -= OnBitmapImageOpened;
                _bitmapImage              = null;
            }
            if (PreviewPlayer.MediaPlayer is not null)
                PreviewPlayer.MediaPlayer.Pause();
            PreviewPlayer.Source = null;
            PreviewImage.Source  = null;
        }
        catch { /* shutdown is best-effort */ }
    }

    // ── Static helpers ───────────────────────────────────────────────────────

    private void ShowError(string message)
    {
        PreviewImage.Visibility  = Visibility.Collapsed;
        PreviewPlayer.Visibility = Visibility.Collapsed;
        ErrorText.Text           = message;
        ErrorPanel.Visibility    = Visibility.Visible;
    }

    private static string FormatDateForHeader(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "";
            var dt    = info.LastWriteTime;
            var today = DateTime.Today;
            if (dt.Date == today)             return $"Today at {dt:h:mm tt}";
            if (dt.Date == today.AddDays(-1)) return $"Yesterday at {dt:h:mm tt}";
            return dt.ToString("MMM d, yyyy · h:mm tt");
        }
        catch { return ""; }
    }

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
        if (bytes < 1_024)     return $"{bytes} B";
        if (bytes < 1_048_576) return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes / 1_048_576.0:F1} MB";
    }
}
