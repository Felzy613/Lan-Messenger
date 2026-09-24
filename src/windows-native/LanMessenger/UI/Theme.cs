using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LanMessenger.UI;

// Central palette for everything the code-behind paints directly (bubbles,
// check marks, presence dots, avatars). Every value comes from GlassTokens,
// which scripts/design/gen_tokens.py generates from design/liquid-glass/
// tokens.json; XAML reads the same tokens as Token*Brush ThemeResources and
// follows theme changes on its own. These brushes are swapped by Initialize(),
// which MainWindow calls at startup, on ActualThemeChanged, and when the user
// turns transparency effects on or off.
public static class Theme
{
    // Shared brush instances — every bubble, sidebar row, and avatar refreshes
    // multiple times per second when chats are busy. Using shared instances
    // instead of per-call `new SolidColorBrush(...)` shaves a huge amount of
    // GC pressure off the UI we render hundreds of times during scroll,
    // typing, and live status updates. Initialize() re-points them at the
    // palette matching the active theme.
    public static SolidColorBrush BrandAccentBrush       { get; private set; } = new(GlassTokens.BrandLight);
    public static SolidColorBrush OutgoingBubbleBrush    { get; private set; } = new(GlassTokens.BubbleOutLight);
    public static SolidColorBrush IncomingBubbleBrush    { get; private set; } = new(GlassTokens.BubbleInLight);
    public static SolidColorBrush ChatBackgroundBrush    { get; private set; } = new(GlassTokens.WallpaperLight);
    public static SolidColorBrush SidebarBackgroundBrush { get; private set; } = new(GlassTokens.SidebarGroundLight);
    public static SolidColorBrush BubbleTextBrush        { get; private set; } = new(GlassTokens.InkLight);

    /// <summary>Sent and delivered ticks, and the meta row, inside an incoming bubble.</summary>
    public static SolidColorBrush CheckGreyBrush         { get; private set; } = new(GlassTokens.InkSecondaryLight);
    /// <summary>The read tick: tick-read, which differs between themes.</summary>
    public static SolidColorBrush CheckBlueBrush         { get; private set; } = new(GlassTokens.TickReadLight);
    public static SolidColorBrush OnlineDotBrush         { get; private set; } = new(GlassTokens.PresenceOnlineLight);
    public static SolidColorBrush OfflineDotBrush        { get; private set; } = new(GlassTokens.PresenceOfflineLight);

    /// <summary>Time, "edited", the relay badge and reply previews in an incoming bubble.</summary>
    public static SolidColorBrush MetaInBrush            { get; private set; } = new(GlassTokens.InkSecondaryLight);
    /// <summary>The same, inside an outgoing bubble, which is itself green.</summary>
    public static SolidColorBrush MetaOutBrush           { get; private set; } = new(GlassTokens.MetaOutLight);
    public static SolidColorBrush AccentInkBrush         { get; private set; } = new(GlassTokens.AccentInkLight);
    public static SolidColorBrush AccentInkOutBrush      { get; private set; } = new(GlassTokens.AccentInkOutLight);
    public static SolidColorBrush OnBrandBrush           { get; private set; } = new(GlassTokens.OnBrandLight);
    public static SolidColorBrush DangerInkBrush         { get; private set; } = new(GlassTokens.DangerInkLight);
    public static SolidColorBrush InkSecondaryBrush      { get; private set; } = new(GlassTokens.InkSecondaryLight);
    public static SolidColorBrush AccentWashBrush        { get; private set; } = new(GlassTokens.AccentWashLight);
    public static SolidColorBrush InsetFillBrush         { get; private set; } = new(GlassTokens.InsetFillLight);

    /// <summary>A failed send's glyph.</summary>
    public static SolidColorBrush BubbleFailedBrush      { get; private set; } = new(GlassTokens.DangerInkLight);
    /// <summary>Muted text: "this message was deleted" and similar placeholders.</summary>
    public static SolidColorBrush MutedTextBrush         { get; private set; } = new(GlassTokens.InkSecondaryLight);

    public static bool IsDark { get; private set; }

    /// <summary>
    /// False when the user has turned transparency effects off (or battery
    /// saver has): translucent bubbles then use the opaque -fallback tokens,
    /// as acrylic does by itself through its FallbackColor.
    /// </summary>
    public static bool TransparencyEnabled { get; private set; } = true;

    /// <summary>
    /// Raised after Initialize swaps the brushes, so controls already on screen
    /// can repaint instead of keeping the previous theme's colours until their
    /// next refresh.
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// Points the shared brushes at the palette for the given theme. Called by
    /// MainWindow once at startup and again on ActualThemeChanged and
    /// AdvancedEffectsEnabledChanged. Fresh brush instances (rather than
    /// mutating .Color in place) keep already-rendered elements stable until
    /// their next refresh.
    /// </summary>
    public static void Initialize(bool isDark, bool transparencyEnabled = true)
    {
        IsDark = isDark;
        TransparencyEnabled = transparencyEnabled;
        static SolidColorBrush B(Color light, Color dark) => new(GlassTokens.Pick(light, dark));

        BrandAccentBrush       = B(GlassTokens.BrandLight, GlassTokens.BrandDark);
        OutgoingBubbleBrush    = transparencyEnabled
            ? B(GlassTokens.BubbleOutLight, GlassTokens.BubbleOutDark)
            : B(GlassTokens.BubbleOutFallbackLight, GlassTokens.BubbleOutFallbackDark);
        IncomingBubbleBrush    = transparencyEnabled
            ? B(GlassTokens.BubbleInLight, GlassTokens.BubbleInDark)
            : B(GlassTokens.BubbleInFallbackLight, GlassTokens.BubbleInFallbackDark);
        ChatBackgroundBrush    = B(GlassTokens.WallpaperLight, GlassTokens.WallpaperDark);
        SidebarBackgroundBrush = B(GlassTokens.SidebarGroundLight, GlassTokens.SidebarGroundDark);
        BubbleTextBrush        = B(GlassTokens.InkLight, GlassTokens.InkDark);
        CheckGreyBrush         = B(GlassTokens.InkSecondaryLight, GlassTokens.InkSecondaryDark);
        CheckBlueBrush         = B(GlassTokens.TickReadLight, GlassTokens.TickReadDark);
        OnlineDotBrush         = B(GlassTokens.PresenceOnlineLight, GlassTokens.PresenceOnlineDark);
        OfflineDotBrush        = B(GlassTokens.PresenceOfflineLight, GlassTokens.PresenceOfflineDark);
        MetaInBrush            = B(GlassTokens.InkSecondaryLight, GlassTokens.InkSecondaryDark);
        MetaOutBrush           = B(GlassTokens.MetaOutLight, GlassTokens.MetaOutDark);
        AccentInkBrush         = B(GlassTokens.AccentInkLight, GlassTokens.AccentInkDark);
        AccentInkOutBrush      = B(GlassTokens.AccentInkOutLight, GlassTokens.AccentInkOutDark);
        OnBrandBrush           = B(GlassTokens.OnBrandLight, GlassTokens.OnBrandDark);
        DangerInkBrush         = B(GlassTokens.DangerInkLight, GlassTokens.DangerInkDark);
        InkSecondaryBrush      = B(GlassTokens.InkSecondaryLight, GlassTokens.InkSecondaryDark);
        AccentWashBrush        = B(GlassTokens.AccentWashLight, GlassTokens.AccentWashDark);
        InsetFillBrush         = B(GlassTokens.InsetFillLight, GlassTokens.InsetFillDark);
        BubbleFailedBrush      = B(GlassTokens.DangerInkLight, GlassTokens.DangerInkDark);
        MutedTextBrush         = B(GlassTokens.InkSecondaryLight, GlassTokens.InkSecondaryDark);

        Changed?.Invoke();
    }

    // One brush per avatar palette colour — shared across every avatar row.
    public static readonly SolidColorBrush[] AvatarBrushes;

    static Theme()
    {
        AvatarBrushes = new SolidColorBrush[AvatarColors.Length];
        for (var i = 0; i < AvatarColors.Length; i++)
            AvatarBrushes[i] = new SolidColorBrush(AvatarColors[i]);
    }

    public static SolidColorBrush AvatarBrush(string name)
        => AvatarBrushes[AvatarPalette.Index(name)];

    // Avatar palette — the same eight avatar-* tokens as macOS -------------------
    //
    // AvatarPalette holds the values and picks one per name; its macOS twin does
    // exactly the same, so a contact looks the same on either machine.

    public static readonly Color[] AvatarColors = Array.ConvertAll(AvatarPalette.Rgb,
        rgb => Color.FromArgb(255,
            (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));

    public static Color AvatarColor(string name)
        => AvatarColors[AvatarPalette.Index(name)];

    public static string Initials(string name)
    {
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
            : (name.Length > 0 ? name[0].ToString().ToUpperInvariant() : "?");
    }

    public static string FormatTimestamp(DateTime? dt)
    {
        if (dt is null) return "";
        var local = dt.Value.ToLocalTime();
        var now   = DateTime.Now;
        if (local.Date == now.Date)        return local.ToString("h:mm tt");
        if (local.Date == now.Date - TimeSpan.FromDays(1)) return "Yesterday";
        if ((now - local).TotalDays < 7)   return local.ToString("ddd");
        return local.ToString("M/d/yy");
    }
}
