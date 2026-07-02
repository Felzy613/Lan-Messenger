using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LanMessenger.UI;

// Central palette for everything the code-behind paints directly (bubbles,
// check marks, avatars). XAML-side colors live in App.xaml ThemeDictionaries
// and follow theme changes automatically; these brushes are swapped by
// Initialize(), which MainWindow calls at startup and again whenever the root
// element's ActualTheme changes.
public static class Theme
{
    // WhatsApp-inspired palette --------------------------------------------------

    /// <summary>Primary brand colour — WhatsApp green.</summary>
    public static readonly Color BrandAccent       = Color.FromArgb(255,  37, 211, 102);
    public static readonly Color BrandAccentDark   = Color.FromArgb(255,  18, 140,  78);

    /// <summary>Outgoing bubble — light WhatsApp green / dark teal.</summary>
    public static readonly Color OutgoingBubble     = Color.FromArgb(255, 220, 248, 198);
    public static readonly Color OutgoingBubbleDark = Color.FromArgb(255,   0,  92,  75);

    /// <summary>Incoming bubble — white / dark grey.</summary>
    public static readonly Color IncomingBubble     = Color.FromArgb(255, 255, 255, 255);
    public static readonly Color IncomingBubbleDark = Color.FromArgb(255,  32,  44,  51);

    /// <summary>Chat background — soft beige / near black.</summary>
    public static readonly Color ChatBackground     = Color.FromArgb(255, 229, 221, 213);
    public static readonly Color ChatBackgroundDark = Color.FromArgb(255,  13,  20,  24);

    /// <summary>Sidebar background.</summary>
    public static readonly Color SidebarBackground     = Color.FromArgb(255, 240, 242, 245);
    public static readonly Color SidebarBackgroundDark = Color.FromArgb(255,  17,  27,  33);

    /// <summary>Bubble text — near black / near white.</summary>
    public static readonly Color BubbleText     = Color.FromArgb(255,  17,  27,  33);
    public static readonly Color BubbleTextDark = Color.FromArgb(255, 233, 237, 239);

    /// <summary>Status check colours (WhatsApp-ish blue for read).</summary>
    public static readonly Color CheckGrey = Color.FromArgb(255, 140, 145, 152);
    public static readonly Color CheckBlue = Color.FromArgb(255,  79, 158, 247);

    /// <summary>Online / offline presence dot fills (same in both themes).</summary>
    public static readonly Color OnlineDot  = Color.FromArgb(255,  37, 211, 102);
    public static readonly Color OfflineDot = Color.FromArgb(255, 142, 142, 147);

    // Shared brush instances — every bubble, sidebar row, and avatar refreshes
    // multiple times per second when chats are busy. Using shared instances
    // instead of per-call `new SolidColorBrush(...)` shaves a huge amount of
    // GC pressure off the UI we render hundreds of times during scroll,
    // typing, and live status updates. Initialize() re-points them at the
    // palette matching the active theme.
    public static SolidColorBrush BrandAccentBrush       { get; private set; } = new(BrandAccent);
    public static SolidColorBrush OutgoingBubbleBrush    { get; private set; } = new(OutgoingBubble);
    public static SolidColorBrush IncomingBubbleBrush    { get; private set; } = new(IncomingBubble);
    public static SolidColorBrush ChatBackgroundBrush    { get; private set; } = new(ChatBackground);
    public static SolidColorBrush SidebarBackgroundBrush { get; private set; } = new(SidebarBackground);
    public static SolidColorBrush CheckGreyBrush         { get; private set; } = new(CheckGrey);
    public static SolidColorBrush CheckBlueBrush         { get; private set; } = new(CheckBlue);
    public static SolidColorBrush BubbleTextBrush        { get; private set; } = new(BubbleText);
    public static SolidColorBrush OnlineDotBrush         { get; private set; } = new(OnlineDot);
    public static SolidColorBrush OfflineDotBrush        { get; private set; } = new(OfflineDot);

    public static SolidColorBrush BubbleFailedBrush { get; private set; } =
        new(Color.FromArgb(255, 220, 60, 60));
    /// <summary>Muted/gray text used for "this message was deleted" placeholders.</summary>
    public static SolidColorBrush MutedTextBrush { get; private set; } =
        new(Color.FromArgb(255, 142, 142, 147));

    public static bool IsDark { get; private set; }

    /// <summary>
    /// Points the shared brushes at the palette for the given theme. Called by
    /// MainWindow once at startup and again on ActualThemeChanged. Fresh brush
    /// instances (rather than mutating .Color in place) keep already-rendered
    /// elements stable until their next refresh.
    /// </summary>
    public static void Initialize(bool isDark)
    {
        IsDark = isDark;
        OutgoingBubbleBrush    = new SolidColorBrush(isDark ? OutgoingBubbleDark : OutgoingBubble);
        IncomingBubbleBrush    = new SolidColorBrush(isDark ? IncomingBubbleDark : IncomingBubble);
        ChatBackgroundBrush    = new SolidColorBrush(isDark ? ChatBackgroundDark : ChatBackground);
        SidebarBackgroundBrush = new SolidColorBrush(isDark ? SidebarBackgroundDark : SidebarBackground);
        BubbleTextBrush        = new SolidColorBrush(isDark ? BubbleTextDark : BubbleText);
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
        => AvatarBrushes[StableHash(name) % AvatarColors.Length];

    // Avatar palette — same 8 colors as macOS version ----------------------------

    public static readonly Color[] AvatarColors =
    [
        Color.FromArgb(255,  74, 144, 226),  // blue
        Color.FromArgb(255,  80, 200, 120),  // green
        Color.FromArgb(255, 255, 149,   0),  // orange
        Color.FromArgb(255, 175,  82, 222),  // purple
        Color.FromArgb(255, 255,  59,  48),  // red
        Color.FromArgb(255,  90, 200, 250),  // teal
        Color.FromArgb(255, 255, 204,   0),  // yellow
        Color.FromArgb(255, 142, 142, 147),  // gray
    ];

    public static Color AvatarColor(string name)
        => AvatarColors[StableHash(name) % AvatarColors.Length];

    // FNV-1a. string.GetHashCode() is randomized per process on .NET, which
    // made every contact's avatar colour reshuffle on each launch.
    private static int StableHash(string name)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in name)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)(hash % int.MaxValue);
        }
    }

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
