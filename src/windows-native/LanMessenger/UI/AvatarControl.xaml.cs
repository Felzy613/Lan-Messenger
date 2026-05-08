using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LanMessenger.UI;

public sealed partial class AvatarControl : UserControl
{
    public static readonly DependencyProperty NameTextProperty =
        DependencyProperty.Register(nameof(NameText), typeof(string), typeof(AvatarControl),
            new PropertyMetadata("", OnNameChanged));

    public string NameText
    {
        get => (string)GetValue(NameTextProperty);
        set => SetValue(NameTextProperty, value);
    }

    public AvatarControl() => InitializeComponent();

    private static void OnNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AvatarControl ctrl) ctrl.Refresh();
    }

    private void Refresh()
    {
        var color   = Theme.AvatarColor(NameText);
        var initials = Theme.Initials(NameText);
        BackgroundEllipse.Fill = new SolidColorBrush(color);
        InitialsText.Text      = initials;
    }
}
