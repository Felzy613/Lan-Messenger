using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LanMessenger.UI;

public static class Theme
{
    // Avatar palette — same 8 colors as macOS version
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
