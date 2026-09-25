using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace LanMessenger.UI;

/// <summary>
/// The glass recipes for controls built in code: dialog content panels and
/// the rows of lists filled at run time. XAML reaches the same styles by key.
/// </summary>
public static class GlassControls
{
    /// A keyed style from Styles/Glass.xaml, or null (the control then keeps
    /// Fluent's look rather than failing).
    public static Style? Style(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    /// A list of rows in glass: GlassListItemStyle, whose own template draws
    /// the hover, press and selected states (a glass-thick lozenge rather than
    /// Fluent's accent pill) at radius-row, focus ring included.
    public static void ApplyList(ListView list)
    {
        if (Style("GlassListItemStyle") is { } row) list.ItemContainerStyle = row;
    }

    /// A check box that fills with brand when checked, with an on-brand tick,
    /// rather than the user's Windows accent colour.
    public static void ApplyBrandCheck(CheckBox box)
    {
        if (IsHighContrast()) return;
        var r = box.Resources;
        var brand   = Brush(GlassTokens.BrandLight, GlassTokens.BrandDark);
        var hover   = Brush(GlassTokens.BrandHoverLight, GlassTokens.BrandHoverDark);
        var pressed = Brush(GlassTokens.BrandPressedLight, GlassTokens.BrandPressedDark);
        var tick    = Brush(GlassTokens.OnBrandLight, GlassTokens.OnBrandDark);
        r["CheckBoxCheckBackgroundFillChecked"]              = brand;
        r["CheckBoxCheckBackgroundFillCheckedPointerOver"]   = hover;
        r["CheckBoxCheckBackgroundFillCheckedPressed"]       = pressed;
        r["CheckBoxCheckBackgroundStrokeChecked"]            = brand;
        r["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = hover;
        r["CheckBoxCheckBackgroundStrokeCheckedPressed"]     = pressed;
        r["CheckBoxCheckGlyphForegroundChecked"]             = tick;
        r["CheckBoxCheckGlyphForegroundCheckedPointerOver"]  = tick;
        r["CheckBoxCheckGlyphForegroundCheckedPressed"]      = tick;
    }

    private static SolidColorBrush Brush(Windows.UI.Color light, Windows.UI.Color dark) =>
        new(GlassTokens.Pick(light, dark));

    private static bool IsHighContrast()
    {
        try { return new AccessibilitySettings().HighContrast; }
        catch (Exception) { return false; }
    }
}
