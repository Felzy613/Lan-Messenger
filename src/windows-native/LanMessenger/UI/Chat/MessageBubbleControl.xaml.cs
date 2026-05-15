using LanMessenger.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        MessageText.Text   = Row.IsFile ? $"📎 {Row.Text}" : Row.Text;
        TimestampText.Text = Row.Timestamp;
        OpenFileBtn.Visibility = Row.IsFile ? Visibility.Visible : Visibility.Collapsed;

        if (Row.Incoming)
        {
            Bubble.HorizontalAlignment = HorizontalAlignment.Left;
            Bubble.Background          = Theme.IncomingBubbleBrush;
            MessageText.Foreground     = new SolidColorBrush(Color.FromArgb(255, 17, 27, 33));
        }
        else
        {
            Bubble.HorizontalAlignment = HorizontalAlignment.Right;
            Bubble.Background          = Theme.OutgoingBubbleBrush;
            MessageText.Foreground     = new SolidColorBrush(Color.FromArgb(255, 17, 27, 33));
        }

        // Reply chip
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

    private void UpdateStatusGlyph()
    {
        if (Row is null || Row.Incoming) { StatusText.Text = ""; return; }
        // WhatsApp-style status:
        //   Sending / Queued: ⏱ clock
        //   Sent          : ✓ single grey check
        //   Delivered     : ✓✓ double grey check
        //   Read          : ✓✓ double BLUE check
        //   Failed        : ✗ red
        switch (Row.Status)
        {
            case "Sending":
            case "Queued":
                StatusText.Text       = "⏱";
                StatusText.Foreground = Theme.CheckGreyBrush;
                break;
            case "Sent":
                StatusText.Text       = "✓";
                StatusText.Foreground = Theme.CheckGreyBrush;
                break;
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
                StatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 60, 60));
                break;
            default:
                StatusText.Text = "";
                break;
        }
    }

    private async void OpenFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Row?.FilePath is { Length: > 0 } path && File.Exists(path))
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await Launcher.LaunchFileAsync(file);
        }
    }

    // MARK: - Context menu actions

    private void ReplyMenu_Click(object sender, RoutedEventArgs e)
    {
        // Walk up the visual tree to the parent ChatPage and ask it to set the reply target.
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
