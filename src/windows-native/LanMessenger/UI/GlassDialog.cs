using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace LanMessenger.UI;

/// <summary>
/// Turns a ContentDialog into a thick-glass sheet: every dialog in the app is
/// built in code, so this is called from each construction site (and from the
/// constructor of each ContentDialog subclass) rather than by an implicit style.
/// </summary>
public static class GlassDialog
{
    /// <param name="destructive">
    /// For confirmations that remove data. The primary button takes the danger
    /// style, and whatever DefaultButton the dialog already has stays: "Delete
    /// conversation?" keeps Cancel as its default.
    /// </param>
    public static void Apply(ContentDialog dialog, bool destructive = false)
    {
        var resources = Application.Current.Resources;
        if (Find<Style>(resources, "GlassDialogStyle") is { } sheet) dialog.Style = sheet;
        // The template's inner DialogSpace grid rounds itself from this resource,
        // not from the dialog's CornerRadius.
        dialog.Resources["OverlayCornerRadius"] = new CornerRadius(GlassTokens.Radius.Sheet);

        if (Find<Style>(resources, destructive ? "GlassDangerButtonStyle" : "GlassPrimaryButtonStyle") is { } primary)
            dialog.PrimaryButtonStyle = primary;
        if (Find<Style>(resources, "GlassButtonStyle") is { } glass)
        {
            dialog.SecondaryButtonStyle = glass;
            dialog.CloseButtonStyle     = glass;
        }

        // Fluent's own brushes, per dialog. The template restyles whichever
        // button is DefaultButton with AccentButtonStyle from a visual state,
        // over the styles set above, and that — like a focused field's
        // underline — reads the user's Windows accent colour. Overriding those
        // keys in App.xaml does nothing: a ThemeResource inside a generic.xaml
        // template resolves from the control's own tree and then the style's
        // own dictionary, before it ever reaches the app's. Per-dialog
        // resources are in the control's tree, so they win.
        // An ordinary dialog's default is brand; a destructive dialog's default
        // is its SAFE button, which must not look like the primary action, so
        // it is glass. High Contrast keeps the system's colours.
        if (IsHighContrast()) return;
        dialog.Resources["TextControlBorderBrushFocused"] =
            new SolidColorBrush(GlassTokens.Pick(GlassTokens.FocusRingLight, GlassTokens.FocusRingDark));
        if (destructive)
            SetAccent(dialog,
                GlassTokens.Pick(GlassTokens.GlassRegularLight, GlassTokens.GlassRegularDark),
                GlassTokens.Pick(GlassTokens.GlassHoverLight, GlassTokens.GlassHoverDark),
                GlassTokens.Pick(GlassTokens.GlassPressedLight, GlassTokens.GlassPressedDark),
                GlassTokens.Pick(GlassTokens.InkLight, GlassTokens.InkDark));
        else
            SetAccent(dialog,
                GlassTokens.Pick(GlassTokens.BrandLight, GlassTokens.BrandDark),
                GlassTokens.Pick(GlassTokens.BrandHoverLight, GlassTokens.BrandHoverDark),
                GlassTokens.Pick(GlassTokens.BrandPressedLight, GlassTokens.BrandPressedDark),
                GlassTokens.Pick(GlassTokens.OnBrandLight, GlassTokens.OnBrandDark));
    }

    private static void SetAccent(ContentDialog dialog, Windows.UI.Color fill, Windows.UI.Color hover,
                                  Windows.UI.Color pressed, Windows.UI.Color ink)
    {
        var r = dialog.Resources;
        r["AccentButtonBackground"]            = new SolidColorBrush(fill);
        r["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        r["AccentButtonBackgroundPressed"]     = new SolidColorBrush(pressed);
        r["AccentButtonForeground"]            = new SolidColorBrush(ink);
        r["AccentButtonForegroundPointerOver"] = new SolidColorBrush(ink);
        r["AccentButtonForegroundPressed"]     = new SolidColorBrush(ink);
        r["AccentButtonBorderBrush"]            = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        r["AccentButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        r["AccentButtonBorderBrushPressed"]     = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private static T? Find<T>(ResourceDictionary resources, string key) where T : class
        => resources.TryGetValue(key, out var value) ? value as T : null;

    private static bool IsHighContrast()
    {
        try { return new AccessibilitySettings().HighContrast; }
        catch (Exception) { return false; }
    }
}
