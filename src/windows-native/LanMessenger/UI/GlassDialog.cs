using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI;

/// <summary>
/// Turns a ContentDialog into a thick-glass sheet: every dialog in the app is
/// built in code, so this is called from each construction site (and from the
/// constructor of each ContentDialog subclass) rather than by an implicit style.
/// </summary>
/// <remarks>
/// GlassDialogStyle carries its own template (see Styles/Glass.xaml), and that
/// is what keeps the buttons in their roles. Fluent's template restyles
/// whichever button is DefaultButton with AccentButtonStyle, over the styles
/// set here, and no resource override reaches the Style it swaps in; per-dialog
/// brush overrides only recoloured a square Fluent button. With the template
/// ours, DefaultButton decides what Enter does and nothing else.
/// </remarks>
public static class GlassDialog
{
    /// <param name="destructive">
    /// For confirmations that remove data. The primary button is glass with a
    /// danger-ink label (GlassDestructiveButtonStyle), not a red fill, and
    /// whatever DefaultButton the dialog already has stays: "Delete
    /// conversation?" keeps Cancel as its default.
    /// </param>
    /// <param name="neutralPrimary">
    /// For a sheet whose primary button is a side trip rather than the thing the
    /// sheet is for ("Add Contact" on New Message, where picking a contact is
    /// the action): it takes the glass style, so no button claims the brand.
    /// </param>
    /// <param name="scrollingBody">
    /// For a sheet whose whole body scrolls (Settings). It gets an opaque
    /// ground (GlassScrollingDialogStyle), which is what lets SheetScrollEdges
    /// dissolve rows into the sheet instead of cutting them at the edge of the
    /// scroll area. The body still has to attach SheetScrollEdges itself.
    /// </param>
    public static void Apply(ContentDialog dialog, bool destructive = false, bool neutralPrimary = false,
                             bool scrollingBody = false)
    {
        var resources = Application.Current.Resources;
        var sheetKey = scrollingBody ? "GlassScrollingDialogStyle" : "GlassDialogStyle";
        if (Find<Style>(resources, sheetKey) is { } sheet) dialog.Style = sheet;

        // Set every time, not left to the style: ContactsDialog re-applies as it
        // moves between states, and a danger style set locally for "Remove
        // contact?" would otherwise outlive that state.
        var primaryKey = destructive ? "GlassDestructiveButtonStyle"
                       : neutralPrimary ? "GlassButtonStyle"
                       : "GlassPrimaryButtonStyle";
        if (Find<Style>(resources, primaryKey) is { } primary)
            dialog.PrimaryButtonStyle = primary;
        if (Find<Style>(resources, "GlassButtonStyle") is { } glass)
        {
            dialog.SecondaryButtonStyle = glass;
            dialog.CloseButtonStyle     = glass;
        }
    }

    private static T? Find<T>(ResourceDictionary resources, string key) where T : class
        => resources.TryGetValue(key, out var value) ? value as T : null;
}
