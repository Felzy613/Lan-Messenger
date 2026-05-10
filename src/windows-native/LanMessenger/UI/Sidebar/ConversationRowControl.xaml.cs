using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI.Sidebar;

public sealed partial class ConversationRowControl : UserControl
{
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(ConversationRowViewModel),
            typeof(ConversationRowControl),
            new PropertyMetadata(null, OnRowChanged));

    public ConversationRowViewModel? Row
    {
        get => (ConversationRowViewModel?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public ConversationRowControl() => InitializeComponent();

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ConversationRowControl ctrl) ctrl.Refresh();
    }

    private void Refresh()
    {
        if (Row is null) return;

        Avatar.NameText      = Row.PeerName;
        NameText.Text        = Row.PeerName;
        PreviewText.Text     = Row.LastMessage;
        TimestampText.Text   = Row.Timestamp;
        OnlineDot.Visibility = Row.IsOnline ? Visibility.Visible : Visibility.Collapsed;

        if (Row.UnreadCount > 0)
        {
            UnreadCount.Text     = Row.UnreadCount > 99 ? "99+" : Row.UnreadCount.ToString();
            UnreadBadge.Visibility = Visibility.Visible;
        }
        else
        {
            UnreadBadge.Visibility = Visibility.Collapsed;
        }
    }
}
