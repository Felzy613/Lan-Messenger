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

    public MessageBubbleControl()
    {
        InitializeComponent();
        // Theme swaps its brushes on a light/dark switch and when transparency
        // effects are turned off; a bubble already on screen repaints then
        // rather than keeping the old colours until its row next changes.
        Loaded   += (_, _) => { if (!_themeHooked) { Theme.Changed += Refresh; _themeHooked = true; } };
        Unloaded += (_, _) => { Theme.Changed -= Refresh; _themeHooked = false; };
    }

    // Loaded can fire again without an Unloaded between (re-parenting), which
    // would subscribe twice and leak one handler past the next Unloaded.
    private bool _themeHooked;

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
        else if (e.PropertyName == nameof(MessageRowViewModel.DeliveredViaRelay))
            UpdateRelayBadge();
        // An inbound edit rewrites the body of a row that is already on screen;
        // a full Refresh is what re-renders the text and the "edited" marker.
        else if (e.PropertyName is nameof(MessageRowViewModel.Text)
                                or nameof(MessageRowViewModel.Edited))
            Refresh();
    }

    private void Refresh()
    {
        if (Row is null) return;

        _mediaKind = Row.IsFile ? MediaTypes.Classify(Row.FilePath) : MediaKind.Other;
        _fileExists = Row.IsFile && !string.IsNullOrEmpty(Row.FilePath) && File.Exists(Row.FilePath);
        // Only offer drag-out for an attachment that still exists — a drag that
        // delivers nothing is worse than no drag at all. Reset on every Refresh
        // because ListView recycles these controls across rows.
        Bubble.CanDrag = _fileExists && !Row.Deleted;

        TimestampText.Text = Row.Timestamp;
        ApplySideBrushes(Row.Incoming);

        // Reset everything to the hidden default so previously-shown panels
        // from a recycled list item don't leak across rows.
        ImageTile.Visibility       = Visibility.Collapsed;
        VideoTile.Visibility       = Visibility.Collapsed;
        FileActions.Visibility     = Visibility.Collapsed;
        FileMissingText.Visibility = Visibility.Collapsed;
        ShowInExplorerMenu.Visibility = Visibility.Collapsed;
        EditedText.Visibility = Row.Edited && !Row.Deleted ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = "";
        MessageText.Visibility = Visibility.Visible;
        MessageText.FontStyle = Windows.UI.Text.FontStyle.Normal;
        MessageText.ClearValue(TextBlock.FontSizeProperty);   // back to the message style's 14
        MessageText.Foreground = Theme.BubbleTextBrush;
        ImagePreview.Source = null;

        // Tail corner: radius-tail on the side that touches the next bubble in
        // the same run, radius-bubble everywhere else. Set once here rather
        // than per content-type branch below, since every branch shares one
        // Border.
        const double r = GlassTokens.Radius.Bubble, tail = GlassTokens.Radius.Tail;
        Bubble.CornerRadius = Row.Incoming
            ? new CornerRadius(r, r, r, Row.IsFirstInRun ? tail : r)
            : new CornerRadius(r, r, Row.IsFirstInRun ? tail : r, r);

        if (Row.Deleted)
        {
            MessageText.Text = "This message was deleted";
            MessageText.FontStyle = Windows.UI.Text.FontStyle.Italic;
            MessageText.FontSize = 13;
            MessageText.Foreground = Row.Incoming ? Theme.MetaInBrush : Theme.MetaOutBrush;
            ReplyChip.Visibility = Visibility.Collapsed;
            ReplyChipThumbnailBorder.Visibility = Visibility.Collapsed;
            RelayBadge.Visibility = Visibility.Collapsed;

            if (Row.Incoming)
            {
                BubbleColumn.HorizontalAlignment = HorizontalAlignment.Left;
                Bubble.Background                = Theme.IncomingBubbleBrush;
            }
            else
            {
                BubbleColumn.HorizontalAlignment = HorizontalAlignment.Right;
                Bubble.Background                = Theme.OutgoingBubbleBrush;
            }
            UpdateStatusGlyph();
            return;
        }

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
            BubbleColumn.HorizontalAlignment = HorizontalAlignment.Left;
            Bubble.Background                = Theme.IncomingBubbleBrush;
        }
        else
        {
            BubbleColumn.HorizontalAlignment = HorizontalAlignment.Right;
            Bubble.Background                = Theme.OutgoingBubbleBrush;
        }
        MessageText.Foreground = Theme.BubbleTextBrush;

        if (!string.IsNullOrEmpty(Row.ReplyToMessageId) && !string.IsNullOrEmpty(Row.ReplyToPreview))
        {
            ReplySender.Text  = Row.ReplyToSender ?? "Reply";
            ReplyPreview.Text = Row.ReplyToPreview ?? "";
            ReplyChip.Visibility = Visibility.Visible;
            SetupReplyChipThumbnail(Row.ReplyFilePath);
        }
        else
        {
            ReplyChip.Visibility = Visibility.Collapsed;
            ReplyChipThumbnailBorder.Visibility = Visibility.Collapsed;
        }

        UpdateRelayBadge();
        UpdateStatusGlyph();
    }

    /// Everything whose colour depends on which side the bubble is on. The
    /// outgoing bubble is itself green, so its meta text is meta-out (on which
    /// ink-secondary fails) and its accents the lighter accent-ink-out.
    private void ApplySideBrushes(bool incoming)
    {
        var meta   = incoming ? Theme.MetaInBrush    : Theme.MetaOutBrush;
        var accent = incoming ? Theme.AccentInkBrush : Theme.AccentInkOutBrush;

        EditedText.Foreground      = meta;
        TimestampText.Foreground   = meta;
        RelayIcon.Foreground       = meta;
        RelayText.Foreground       = meta;
        FileMissingText.Foreground = meta;
        VideoHintText.Foreground   = meta;
        ReplyPreview.Foreground    = meta;
        ReplyChipIcon.Foreground   = meta;
        ReplySender.Foreground     = accent;
        OpenFileBtn.Foreground       = accent;
        ShowInExplorerBtn.Foreground = meta;

        // A bubble always keeps 60px clear on the far side.
        BubbleColumn.Margin = incoming ? new Thickness(0, 0, 60, 0) : new Thickness(60, 0, 0, 0);
    }

    private void UpdateRelayBadge()
    {
        if (Row is null || Row.Deleted) return;
        RelayBadge.Visibility = Row.DeliveredViaRelay ? Visibility.Visible : Visibility.Collapsed;
    }

    // Segoe MDL2 glyphs used for reply chip icons.
    private const string GlyphPhoto    = ""; // Photo
    private const string GlyphVideo    = ""; // Play (video poster)
    private const string GlyphDocument = ""; // Page/document

    /// <summary>
    /// Populates the reply chip thumbnail slot with a small image for image replies,
    /// a media/doc icon for video and file replies, or hides it for text replies.
    /// Falls back silently if the file is missing or the image fails to decode.
    /// </summary>
    private void SetupReplyChipThumbnail(string? path)
    {
        // Reset all thumbnail/icon state.
        ReplyChipThumbnailBorder.Visibility = Visibility.Collapsed;
        ReplyChipImage.Source               = null;
        ReplyChipImage.Visibility           = Visibility.Collapsed;
        ReplyChipIcon.Visibility            = Visibility.Collapsed;

        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var kind = MediaTypes.Classify(path);
        ReplyChipThumbnailBorder.Visibility = Visibility.Visible;

        switch (kind)
        {
            case MediaKind.Image:
                try
                {
                    var bmp = new BitmapImage
                    {
                        DecodePixelWidth  = 72,
                        DecodePixelType   = DecodePixelType.Logical,
                        // See ShowImageInline — must be set before UriSource.
                        CreateOptions     = BitmapCreateOptions.IgnoreImageCache,
                    };
                    bmp.UriSource       = new Uri(path);
                    ReplyChipImage.Source    = bmp;
                    ReplyChipImage.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    LanLogger.Warn("MessageBubble", $"reply chip image load failed for {path}: {ex.Message}");
                    ReplyChipIcon.Glyph      = GlyphPhoto;
                    ReplyChipIcon.Visibility = Visibility.Visible;
                }
                break;

            case MediaKind.Video:
                ReplyChipIcon.Glyph      = GlyphVideo;
                ReplyChipIcon.Visibility = Visibility.Visible;
                break;

            default:
                ReplyChipIcon.Glyph      = GlyphDocument;
                ReplyChipIcon.Visibility = Visibility.Visible;
                break;
        }
    }

    /// <summary>
    /// Loads an image into the inline tile.  DecodePixelWidth caps the in-memory
    /// pixel size so a 4K JPEG doesn't allocate ~30 MB just to render at 280 px.
    /// Errors are caught silently — the tile collapses and we fall back to the
    /// generic file caption.
    ///
    /// IgnoreImageCache is not optional here.  XAML caches decoded bitmaps by
    /// URI for the life of the process, and a chat bubble is only a pointer to
    /// an absolute path — so when a user re-exports an image over the same
    /// filename and sends it again, every bubble for that path keeps rendering
    /// the version decoded first, while the peer receives the new bytes.  That
    /// made the sent bubble disagree with what was actually transmitted.
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
                // Must be assigned before UriSource: setting UriSource starts
                // the decode, and CreateOptions is read at that moment.
                CreateOptions    = BitmapCreateOptions.IgnoreImageCache,
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
        // and any unset value) collapses to a single check — no clocks, no
        // "queued" indicator. Sent and Delivered take the outgoing bubble's
        // meta colour, Read is tick-read, Failed is danger-ink. Ticks only
        // ever sit in outgoing bubbles.
        switch (Row.Status)
        {
            case "Delivered":
                StatusText.Text       = "✓✓";
                StatusText.Foreground = Theme.MetaOutBrush;
                break;
            case "Read":
                StatusText.Text       = "✓✓";
                StatusText.Foreground = Theme.CheckBlueBrush;
                break;
            case "Failed":
                StatusText.Text       = "✗";
                StatusText.Foreground = Theme.DangerInkBrush;
                break;
            default:
                StatusText.Text       = "✓";
                StatusText.Foreground = Theme.MetaOutBrush;
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

    /// <summary>
    /// Hands the attachment to the drop target as a real file, so Explorer and
    /// Outlook copy the file itself rather than receiving a path string.
    /// </summary>
    private async void Bubble_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        var path = Row?.FilePath;
        if (Row is null || !Row.IsFile || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            args.Cancel = true;
            return;
        }

        // StorageFile.GetFileFromPathAsync is async, and the drag starts the
        // moment this handler returns — without the deferral the data package
        // would still be empty when the shell reads it.
        var deferral = args.GetDeferral();
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            args.Data.SetStorageItems(new[] { file });
            args.Data.RequestedOperation = DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            LanLogger.Warn("MessageBubble", $"drag out failed for {path}: {ex.Message}");
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void MediaTile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Row?.FilePath is not { Length: > 0 } path || !File.Exists(path))
        {
            await ShowErrorAsync("File not found", "The file may have been moved or deleted.");
            return;
        }
        try
        {
            var win = new MediaPreviewWindow(path, _mediaKind, Row?.Text ?? Path.GetFileName(path));
            win.Activate();
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
            GlassDialog.Apply(dialog);
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

    private void EditMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Row is null || !Row.IsEditable) return;
        var chatPage = FindParent<ChatPage>();
        chatPage?.RequestEditMessage(Row.MessageId);
    }

    private void CopyMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Row is null) return;
        var pkg = new DataPackage();
        pkg.SetText(Row.IsFile ? Row.FilePath : Row.Text);
        Clipboard.SetContent(pkg);
    }

    private void BubbleMenu_Opening(object? sender, object e)
    {
        if (Row is null) return;
        ReplyMenu.Visibility = Row.CanReply ? Visibility.Visible : Visibility.Collapsed;
        // Only our own outgoing text messages can be edited; the row carries
        // AppModel.IsEditable's answer, which is what EditMessage enforces.
        EditMenu.Visibility = Row.IsEditable ? Visibility.Visible : Visibility.Collapsed;
        // Copy is already hidden implicitly by being meaningless on a placeholder,
        // but leave it visible — it just copies the empty deleted text.
        DeleteForEveryoneMenu.Visibility = Row.CanDeleteForEveryone ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeleteForMeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Row is null) return;
        var chatPage = FindParent<ChatPage>();
        chatPage?.RequestDeleteMessage(Row.MessageId, Row.Incoming, Row.Text, Row.IsFile, Row.FilePath, Row.Timestamp, forEveryone: false);
    }

    private void DeleteForEveryoneMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Row is null || !Row.CanDeleteForEveryone) return;
        var chatPage = FindParent<ChatPage>();
        chatPage?.RequestDeleteMessage(Row.MessageId, Row.Incoming, Row.Text, Row.IsFile, Row.FilePath, Row.Timestamp, forEveryone: true);
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
