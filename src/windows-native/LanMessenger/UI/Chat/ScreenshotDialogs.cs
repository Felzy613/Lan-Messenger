using LanMessenger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LanMessenger.UI.Chat;

// ── Window picker ────────────────────────────────────────────────────────────

/// <summary>
/// Step 1 of the screenshot flow: the user selects which window (or the full
/// screen) to capture.  <see cref="SelectedHwnd"/> is <c>IntPtr.Zero</c> when
/// the user picks "Full Screen".
/// </summary>
internal sealed class ScreenshotWindowPickerDialog : ContentDialog
{
    public IntPtr SelectedHwnd { get; private set; } = IntPtr.Zero;

    private readonly ListView _list;

    // Small wrapper so we can safely retrieve the HWND from a ListView item tag.
    private sealed class WindowEntry { public IntPtr Hwnd { get; init; } }

    public ScreenshotWindowPickerDialog()
    {
        Title             = "Select Window to Capture";
        PrimaryButtonText = "Capture";
        CloseButtonText   = "Cancel";
        DefaultButton     = ContentDialogButton.Primary;

        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinWidth      = 400,
            MinHeight     = 120,
            MaxHeight     = 440,
        };

        // "Full Screen" is always the first option.
        _list.Items.Add(BuildRow(
            icon:     "\uE7F4",
            title:    "Full Screen",
            subtitle: "Capture the entire primary display",
            tag:      new WindowEntry { Hwnd = IntPtr.Zero }));

        // Enumerate visible windows and add one row per window.
        foreach (var win in ScreenshotService.GetVisibleWindows())
        {
            _list.Items.Add(BuildRow(
                icon:     "\uE8A7",
                title:    win.Title,
                subtitle: "",
                tag:      new WindowEntry { Hwnd = win.Hwnd }));
        }

        _list.SelectedIndex = 0;
        Content = _list;

        PrimaryButtonClick += (_, _) =>
        {
            if (_list.SelectedItem is Grid row && row.Tag is WindowEntry entry)
                SelectedHwnd = entry.Hwnd;
        };
    }

    private static Grid BuildRow(string icon, string title, string subtitle, WindowEntry tag)
    {
        var iconBlock = new TextBlock
        {
            FontFamily          = new FontFamily("Segoe MDL2 Assets"),
            Text                = icon,
            FontSize            = 18,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground          = Application.Current.Resources.TryGetValue(
                                      "TextFillColorSecondaryBrush", out var br)
                                      ? (Brush)br
                                      : new SolidColorBrush(Microsoft.UI.Colors.Gray),
        };

        var titleBlock = new TextBlock
        {
            Text  = title,
            Style = Application.Current.Resources.TryGetValue("BodyStrongTextBlockStyle", out var s)
                        ? (Style)s : null,
        };

        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(titleBlock);
        if (!string.IsNullOrEmpty(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text     = subtitle,
                Opacity  = 0.6,
                FontSize = 11,
            });
        }

        var row = new Grid
        {
            ColumnSpacing = 12,
            Padding       = new Thickness(4, 6, 4, 6),
            Tag           = tag,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(stack, 1);
        row.Children.Add(iconBlock);
        row.Children.Add(stack);
        return row;
    }
}

// ── Screenshot preview + confirm ─────────────────────────────────────────────

/// <summary>
/// Step 2 of the screenshot flow: shows the captured image and lets the user
/// decide whether to send it.  Returns <see cref="ContentDialogResult.Primary"/>
/// when the user clicks "Send", <c>None</c> / <c>Secondary</c> on cancel.
/// The caller is responsible for deleting the temp file if the user cancels.
/// </summary>
internal sealed class ScreenshotPreviewDialog : ContentDialog
{
    public ScreenshotPreviewDialog(string imagePath)
    {
        Title             = "Screenshot Preview";
        PrimaryButtonText = "Send";
        CloseButtonText   = "Cancel";
        DefaultButton     = ContentDialogButton.Primary;

        // BitmapImage accepts file:/// URIs built from local absolute paths.
        Image image;
        try
        {
            var uri = new Uri(imagePath);
            image = new Image
            {
                Source              = new BitmapImage(uri),
                MaxWidth            = 560,
                MaxHeight           = 380,
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        catch
        {
            // If image load fails, show a placeholder so the dialog is still usable.
            image = new Image { MaxWidth = 560, MaxHeight = 380 };
        }

        var filename = new TextBlock
        {
            Text                = Path.GetFileName(imagePath),
            Opacity             = 0.6,
            FontSize            = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var root = new StackPanel { Spacing = 8, Width = 580 };
        root.Children.Add(image);
        root.Children.Add(filename);
        Content = root;
    }
}
