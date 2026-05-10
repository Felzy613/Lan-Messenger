using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

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
        if (d is MessageBubbleControl ctrl) ctrl.Refresh();
    }

    private void Refresh()
    {
        if (Row is null) return;

        MessageText.Text   = Row.IsFile ? $"📎 {Row.Text}" : Row.Text;
        TimestampText.Text = Row.Timestamp;
        StatusText.Text    = Row.Incoming ? "" : FormatStatus(Row.Status);

        OpenFileBtn.Visibility = Row.IsFile ? Visibility.Visible : Visibility.Collapsed;

        var resources = Application.Current.Resources;
        if (Row.Incoming)
        {
            Bubble.HorizontalAlignment = HorizontalAlignment.Left;
            Bubble.Background          = (Brush)resources["ControlFillColorSecondaryBrush"];
            MessageText.Foreground     = (Brush)resources["TextFillColorPrimaryBrush"];
        }
        else
        {
            Bubble.HorizontalAlignment = HorizontalAlignment.Right;
            Bubble.Background          = (Brush)resources["AccentFillColorDefaultBrush"];
            MessageText.Foreground     = new SolidColorBrush(Colors.White);
        }
    }

    private static string FormatStatus(string status) => status switch
    {
        "sent"      => "✓",
        "delivered" => "✓✓",
        "read"      => "✓✓ Read",
        "pending"   => "…",
        "failed"    => "✗",
        _           => "",
    };

    private async void OpenFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Row?.FilePath is { Length: > 0 } path && File.Exists(path))
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await Launcher.LaunchFileAsync(file);
        }
    }
}
