using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LanMessenger.UI;

public static class Theme
{
    // WhatsApp-inspired palette --------------------------------------------------

    /// <summary>Primary brand colour — WhatsApp green.</summary>
    public static readonly Color BrandAccent       = Color.FromArgb(255,  37, 211, 102);
    public static readonly Color BrandAccentDark   = Color.FromArgb(255,  18, 140,  78);

    /// <summary>Outgoing bubble — light WhatsApp green.</summary>
    public static readonly Color OutgoingBubble     = Color.FromArgb(255, 220, 248, 198);
    public static readonly Color OutgoingBubbleDark = Color.FromArgb(255,   0,  92,  75);

    /// <summary>Incoming bubble — white / dark grey.</summary>
    public static readonly Color IncomingBubble     = Color.FromArgb(255, 255, 255, 255);
    public static readonly Color IncomingBubbleDark = Color.FromArgb(255,  32,  44,  51);

    /// <summary>Chat background — soft beige / black.</summary>
    public static readonly Color ChatBackground     = Color.FromArgb(255, 229, 221, 213);
    public static readonly Color ChatBackgroundDark = Color.FromArgb(255,  13,  20,  24);

    /// <summary>Sidebar background.</summary>
    public static readonly Color SidebarBackground     = Color.FromArgb(255, 240, 242, 245);
    public static readonly Color SidebarBackgroundDark = Color.FromArgb(255,  17,  27,  33);

    /// <summary>Status check colours (WhatsApp-ish blue for read).</summary>
    public static readonly Color CheckGrey = Color.FromArgb(255, 140, 145, 152);
    public static readonly Color CheckBlue = Color.FromArgb(255,  79, 158, 247);

    public static SolidColorBrush BrandAccentBrush      => new(BrandAccent);
    public static SolidColorBrush OutgoingBubbleBrush   => new(OutgoingBubble);
    public static SolidColorBrush IncomingBubbleBrush   => new(IncomingBubble);
    public static SolidColorBrush ChatBackgroundBrush   => new(ChatBackground);
    public static SolidColorBrush SidebarBackgroundBrush=> new(SidebarBackground);
    public static SolidColorBrush CheckGreyBrush        => new(CheckGrey);
    public static SolidColorBrush CheckBlueBrush        => new(CheckBlue);

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
    {
        var idx = Math.Abs(name.GetHashCode()) % AvatarColors.Length;
        return AvatarColors[idx];
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
