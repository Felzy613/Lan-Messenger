using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using Windows.UI;

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

    // The owning sidebar control sets this so the menu actions can reach AppModel.
    public AppModel? Model { get; set; }

    public ConversationRowControl()
    {
        InitializeComponent();
        // Smaller than the thread's dots and in accent-ink, which is what the
        // word "typing..." used to be; the colour is set in Refresh so a theme
        // switch reaches it.
        TypingDots.DotDiameter = 6;
        TypingDots.DotSpacing  = 4;
        Loaded   += (_, _) => { if (!_themeHooked) { Theme.Changed += Refresh; _themeHooked = true; } };
        Unloaded += (_, _) => { Theme.Changed -= Refresh; _themeHooked = false; };
    }

    private bool _themeHooked;

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ConversationRowControl ctrl) return;
        if (e.OldValue is ConversationRowViewModel old) old.PropertyChanged -= ctrl.OnPropChanged;
        if (e.NewValue is ConversationRowViewModel nw)  nw.PropertyChanged  += ctrl.OnPropChanged;
        ctrl.Refresh();
    }

    private void OnPropChanged(object? s, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (Row is null) return;

        Avatar.NameText        = Row.PeerName;
        Avatar.PhotoB64        = Row.PhotoB64;
        NameText.Text          = Row.PeerName;
        PreviewText.Text       = Row.LastMessage;
        PreviewText.Visibility   = Row.IsTyping ? Visibility.Collapsed : Visibility.Visible;
        TypingCapsule.Visibility = Row.IsTyping ? Visibility.Visible : Visibility.Collapsed;
        TypingDots.DotBrush      = Theme.AccentInkBrush;
        TypingDots.IsActive      = Row.IsTyping;
        TimestampText.Text       = Row.Timestamp;
        // With unread messages the time turns semibold accent-ink, the way
        // WhatsApp marks a conversation that has something new.
        var unread = Row.UnreadCount > 0;
        TimestampText.Foreground = unread ? Theme.AccentInkBrush : Theme.InkSecondaryBrush;
        TimestampText.FontWeight = unread ? Microsoft.UI.Text.FontWeights.SemiBold
                                          : Microsoft.UI.Text.FontWeights.Normal;
        // Always show the dot; green when online, muted gray when offline — matches
        // macOS sidebar. (The previous 45%-alpha black was invisible in dark mode.)
        OnlineDot.Fill = Row.IsOnline ? Theme.OnlineDotBrush : Theme.OfflineDotBrush;

        if (Row.UnreadCount > 0)
        {
            UnreadCount.Text       = Row.UnreadCount > 99 ? "99+" : Row.UnreadCount.ToString();
            UnreadBadge.Visibility = Visibility.Visible;
        }
        else
        {
            UnreadBadge.Visibility = Visibility.Collapsed;
        }

        ArchiveItem.Visibility   = Row.IsArchived ? Visibility.Collapsed : Visibility.Visible;
        UnarchiveItem.Visibility = Row.IsArchived ? Visibility.Visible   : Visibility.Collapsed;
    }

    private void OptionsBtn_Click(object sender, RoutedEventArgs e)
    {
        // The Button has Flyout attached — clicking auto-opens it. We just need to
        // make sure the click doesn't bubble up and select the conversation.
        if (sender is Button btn) btn.Flyout?.ShowAt(btn);
    }

    private void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (Row is null || Model is null) return;
        Model.ArchiveConversation(Row.ConversationId);
    }

    private void Unarchive_Click(object sender, RoutedEventArgs e)
    {
        if (Row is null || Model is null) return;
        Model.UnarchiveConversation(Row.ConversationId);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Row is null || Model is null) return;
        var dialog = new ContentDialog
        {
            Title = "Delete conversation?",
            Content = $"All messages with {Row.PeerName} will be removed from this device. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        GlassDialog.Apply(dialog, destructive: true);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            Model.DeleteConversation(Row.ConversationId);
    }
}
