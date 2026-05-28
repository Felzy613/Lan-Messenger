using LanMessenger.Core.Services;
using LanMessenger.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace LanMessenger.UI.Chat;

public sealed partial class MessageBubbleControl : UserControl
{
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(MessageRowViewModel),
            typeof(MessageBubbleControl),
            new PropertyMetadata(null, OnRowChanged));

    public MessageRowViewModel? Row
    {
        get => (MessageRowViewModel?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    // Cached MediaKind so Refresh() doesn't re-classify on every status update.
    private MediaKind _mediaKind = MediaKind.Other;
    private bool      _fileExists;

    public MessageBubbleControl() => InitializeComponent();

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MessageBubbleControl ctrl) return;
        // Detach any prior status-watcher.
        if (e.OldValue is MessageRowViewModel old) old.PropertyChanged -= ctrl.OnRowPropertyChanged;
        if (e.NewValue is MessageRowViewModel nw)  nw.PropertyChanged  += ctrl.OnRowPropertyChanged;
        ctrl.Refresh();
    }

    private void OnRowPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageRowViewModel.Status))
            UpdateStatusGlyph();
    }

    private void Refresh()
    {
        if (Row is null) return;

        _mediaKind = Row.IsFile ? MediaTypes.Classify(Row.FilePath) : MediaKind.Other;
        _fileExists = Row.IsFile && !string.IsNullOrEmpty(Row.FilePath) && File.Exists(Row.FilePath);

        TimestampText.Text = Row.Timestamp;

        // Reset everything to the hidden default so previously-shown panels
        // from a recycled list item don't leak across rows.
        ImageTile.Visibility       = Visibility.Collapsed;
        VideoTile.Visibility       = Visibility.Collapsed;
        FileActions.Visibility     = Visibility.Collapsed;
        FileMissingText.Visibility = Visibility.Collapsed;
        ShowInExplorerMenu.Visibility = Visibility.Collapsed;
        MessageText.Text = "";
        MessageText.Visibility = Visibility.Visible;
        ImagePreview.Source = null;

        if (Row.IsFile)
        {
            ShowInExplorerMenu.Visibility = _fileExists ? Visibility.Visible : Visibility.Collapsed;

            if (!_fileExists)
            {
                // Missing file — fall back to the generic file caption + warning.
                MessageText.Text = $"📎 {Row.Text}";
                FileMissingText.Visibility = Visibility.Visible;
            }
            else if (_mediaKind == MediaKind.Image)
            {
                ShowImageInline(Row.FilePath);
                MessageText.Visibility = Visibility.Collapsed;
            }
            else if (_mediaKind == MediaKind.Video)
            {
                VideoFilename.Text = Row.Text;
                VideoTile.Visibility = Visibility.Visible;
                MessageText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Generic file (document, archive, etc.).
                MessageText.Text = $"📎 {Row.Text}";
                FileActions.Visibility = Visibility.Visible;
            }
        }
        else
        {
            MessageText.Text = Row.Text;
        }

        // Bubble side / colour + reply chip + status.
        if (Row.Incoming)
        {
            Bubble.HorizontalAlignment = HorizontalAlignment.Left;
            Bubble.Background          = Theme.IncomingBubbleBrush;
        }
        else
        {
            Bubble.HorizontalAlignment = HorizontalAlignment.Right;
            Bubble.Background          = Theme.OutgoingBubbleBrush;
        }
        MessageText.Foreground = Theme.BubbleTextBrush;

        if (!string.IsNullOrEmpty(Row.ReplyToMessageId) && !string.IsNullOrEmpty(Row.ReplyToPreview))
        {
            ReplySender.Text  = Row.ReplyToSender ?? "Reply";
            ReplyPreview.Text = Row.ReplyToPreview ?? "";
            ReplyChip.Visibility = Visibility.Visible;
        }
        else
        {
            ReplyChip.Visibility = Visibility.Collapsed;
        }

        UpdateStatusGlyph();
    }

    /// <summary>
    /// Loads an image into the inline tile.  DecodePixelWidth caps the in-memory
    /// pixel size so a 4K JPEG doesn't allocate ~30 MB just to render at 280 px.
    /// Errors are caught silently — the tile collapses and we fall back to the
    /// generic file caption.
    /// </summary>
    private void ShowImageInline(string path)
    {
        try
        {
            var bmp = new BitmapImage
            {
                // Cap the decoded pixel width for chat-list rendering. Setting
                // only DecodePixelWidth preserves aspect ratio.
                DecodePixelWidth = 560,
                DecodePixelType  = DecodePixelType.Logical,
            };
            bmp.UriSource = new Uri(path);
            ImagePreview.Source = bmp;
            ImageTile.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            LanLogger.Warn("MessageBubble", $"inline image load failed for {path}: {ex.Message}");
            // Fall back to a regular file bubble so the user can still open it.
            ImageTile.Visibility = Visibility.Collapsed;
            MessageText.Text = $"📎 {Row?.Text ?? ""}";
            MessageText.Visibility = Visibility.Visible;
            FileActions.Visibility = Visibility.Visible;
        }
    }

    private void UpdateStatusGlyph()
    {
        if (Row is null || Row.Incoming) { StatusText.Text = ""; return; }
        // Modern-messenger style: every pre-delivery state (Sending/Queued/Sent
        // and any unset value) collapses to a single grey check — no clocks, no
        // "queued" indicator. Delivered = double grey, Read = double blue,
        // Failed = red ✗.
        switch (Row.Status)
        {
            case "Delivered":
                StatusText.Text       = "✓✓";
                StatusText.Foreground = Theme.CheckGreyBrush;
                break;
            case "Read":
                StatusText.Text       = "✓✓";
                StatusText.Foreground = Theme.CheckBlueBrush;
                break;
            case "Failed":
                StatusText.Text       = "✗";
                StatusText.Foreground = Theme.BubbleFailedBrush;
                break;
            default:
                StatusText.Text       = "✓";
                StatusText.Foreground = Theme.CheckGreyBrush;
                break;
        }
    }

    // MARK: - File actions

    private async void OpenFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Row?.FilePath is { Length: > 0 } path && File.Exists(path))
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                await Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                LanLogger.Warn("MessageBubble", $"open file failed for {path}: {ex.Message}");
                await ShowErrorAsync("Could not open file", ex.Message);
            }
        }
        else
        {
            await ShowErrorAsync("File not found", "The file may have been moved or deleted.");
        }
    }

    private async void ShowInExplorerBtn_Click(object sender, RoutedEventArgs e) =>
        await RevealInExplorerAsync();

    private async void ShowInExplorerMenu_Click(object sender, RoutedEventArgs e) =>
        await RevealInExplorerAsync();

    private async void MediaTile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Row?.FilePath is not { Length: > 0 } path || !File.Exists(path))
        {
            await ShowErrorAsync("File not found", "The file may have been moved or deleted.");
            return;
        }
        try
        {
            // ContentDialog requires an XamlRoot — the bubble's XamlRoot works
            // because it inherits from the active visual tree.
            var dialog = new MediaPreviewDialog(path, _mediaKind, Row?.Text ?? Path.GetFileName(path))
            {
                XamlRoot = this.XamlRoot,
            };
            _ = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            LanLogger.Error("MessageBubble", $"media preview failed for {path}", ex);
            await ShowErrorAsync("Could not preview file", ex.Message);
        }
    }

    private async Task RevealInExplorerAsync()
    {
        if (Row?.FilePath is not { Length: > 0 } path)
        {
            await ShowErrorAsync("File not found", "The file path is missing from this message.");
            return;
        }
        var error = await FileReveal.RevealAsync(path);
        if (error is not null)
        {
            await ShowErrorAsync("Cannot show file", error);
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            };
            _ = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Last-ditch logging if the dialog itself can't be shown
            // (rare — happens during teardown when XamlRoot is gone).
            LanLogger.Warn("MessageBubble", $"error dialog failed: {ex.Message} (original: {title}: {message})");
        }
    }

    // MARK: - Existing context menu actions

    private void ReplyMenu_Click(object sender, RoutedEventArgs e)
    {
        var chatPage = FindParent<ChatPage>();
        chatPage?.RequestReplyTo(Row?.MessageId);
    }

    private void CopyMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Row is null) return;
        var pkg = new DataPackage();
        pkg.SetText(Row.IsFile ? Row.FilePath : Row.Text);
        Clipboard.SetContent(pkg);
    }

    private void ReplyChip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Tap on reply chip scrolls to the referenced message (handled by ChatPage in the future).
        // For now, no-op — the visual cue is enough.
    }

    private T? FindParent<T>() where T : DependencyObject
    {
        DependencyObject? d = this;
        while (d is not null)
        {
            d = VisualTreeHelper.GetParent(d);
            if (d is T t) return t;
        }
        return null;
    }
}
