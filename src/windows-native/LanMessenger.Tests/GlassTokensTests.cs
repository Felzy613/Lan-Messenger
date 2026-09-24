using LanMessenger.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.UI;

namespace LanMessenger.Tests;

// Guards the generated design tokens. The spot values prove the generator read
// tokens.json the way the design system means it; the contrast floors stop a
// later "tune" of one colour from quietly taking text below what people can
// read. A surface is the glass composited over the wallpaper and over the
// wallpaper's green glow, whichever is worse, because that is what a
// translucent surface actually sits on. Mirror of GlassTokensTests.swift.
[TestClass]
public class GlassTokensTests
{
    [TestMethod]
    public void SpotValuesMatchTokensJson()
    {
        Assert.AreEqual(0xFF25D366u, Argb(GlassTokens.BrandLight));
        Assert.AreEqual(0xFF25D366u, Argb(GlassTokens.BrandDark));
        Assert.AreEqual(0xE0005C4Bu, Argb(GlassTokens.BubbleOutDark));
        // {accent-ink} resolves per theme, not once.
        Assert.AreEqual(GlassTokens.AccentInkLight, GlassTokens.AccentInkOutLight);
        Assert.AreEqual(GlassTokens.SidebarGroundDark, GlassTokens.GlassRegularFallbackDark);
        Assert.AreEqual(16.0, GlassTokens.Radius.Bubble);
        Assert.AreEqual(420.0, GlassTokens.Size.BubbleMax);
        Assert.AreEqual(12.0, GlassTokens.Space.S12);
    }

    // AvatarPalette keeps its own copy so it stays free of WinRT types. This is
    // what stops the two drifting apart.
    [TestMethod]
    public void AvatarPaletteIsTheAvatarTokensInOrder()
    {
        var tokens = GlassTokens.All
            .Where(t => t.Name.StartsWith("avatar-", StringComparison.Ordinal))
            .Select(t => Argb(t.Light) & 0xFFFFFF)
            .ToList();
        CollectionAssert.AreEqual(AvatarPalette.Rgb.ToList(), tokens);
    }

    private static readonly (string Foreground, string[][] Surfaces, double Minimum)[] Floors =
    [
        ("ink", [["bubble-in"], ["bubble-out"], ["glass-regular"], ["glass-thick"]], 7.0),
        ("ink-secondary", [["bubble-in"], ["glass-regular"], ["glass-thick"], ["sidebar-ground"]], 4.5),
        ("meta-out", [["bubble-out"]], 4.5),
        ("accent-ink", [["bubble-in"], ["glass-regular"], ["glass-thick"]], 4.5),
        ("accent-ink-out", [["bubble-out"]], 4.5),
        ("on-brand", [["brand"]], 4.5),
        ("on-warning", [["warning"]], 4.5),
        ("hud-ink", [["hud-surface"], ["hud-surface", "hud-button"]], 7.0),
        ("hud-ink-secondary", [["hud-surface"]], 4.5),
        ("hud-signal-view", [["hud-surface"]], 3.0),
        ("hud-signal-control", [["hud-surface"]], 3.0),
        ("ink-inverse", [["danger"], .. GlassTokens.All
            .Where(t => t.Name.StartsWith("avatar-", StringComparison.Ordinal))
            .Select(t => new[] { t.Name })], 4.5),
        ("tick-read", [["bubble-out"]], 3.0),
        ("focus-ring", [["wallpaper"], ["sidebar-ground"]], 3.0),
        // Transparency off: the opaque fallbacks must still read.
        ("ink", [["bubble-in-fallback"], ["bubble-out-fallback"],
                 ["glass-regular-fallback"], ["glass-thick-fallback"]], 4.5),
        ("ink-secondary", [["bubble-in-fallback"]], 4.5),
        ("meta-out", [["bubble-out-fallback"]], 4.5),
        // Washes sit on glass: the Open pill, the typing capsule, the consent warning.
        ("accent-ink", [["bubble-in", "accent-wash"], ["glass-regular", "accent-wash"],
                        ["glass-thick", "accent-wash"]], 4.5),
        ("warning-ink", [["glass-regular", "warning-wash"], ["glass-thick", "warning-wash"]], 4.5),
    ];

    [TestMethod]
    public void EveryPairingMeetsItsContrastFloor()
    {
        foreach (var (foreground, surfaces, minimum) in Floors)
        foreach (var surface in surfaces)
        foreach (var dark in new[] { false, true })
        {
            var ratio = WorstContrast(foreground, surface, dark);
            Assert.IsTrue(ratio >= minimum,
                $"{foreground} on {string.Join(" over ", surface)} ({(dark ? "dark" : "light")}) " +
                $"is {ratio:0.00}:1, below {minimum}");
        }
    }

    // The arithmetic has to be right for the floors to mean anything.
    [TestMethod]
    public void ContrastMatchesWcagReferencePoints()
    {
        Assert.AreEqual(21.0, Ratio(Rgb(0xFF000000), Rgb(0xFFFFFFFF)), 0.001);
        Assert.AreEqual(4.48, Ratio(Rgb(0xFF777777), Rgb(0xFFFFFFFF)), 0.01);
    }

    // ── WCAG 2.x ────────────────────────────────────────────────────────────

    private static uint Argb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static uint Value(string name, bool dark)
    {
        var token = GlassTokens.All.FirstOrDefault(t => t.Name == name);
        Assert.IsNotNull(token.Name, $"no token named {name}");
        return Argb(dark ? token.Dark : token.Light);
    }

    private static (double R, double G, double B) Rgb(uint argb) =>
        ((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);

    private static (double R, double G, double B) Over(uint top, (double R, double G, double B) bottom)
    {
        var alpha = ((top >> 24) & 0xFF) / 255.0;
        var t = Rgb(top);
        return (t.R * alpha + bottom.R * (1 - alpha),
                t.G * alpha + bottom.G * (1 - alpha),
                t.B * alpha + bottom.B * (1 - alpha));
    }

    private static double Luminance((double R, double G, double B) c)
    {
        static double Channel(double v)
        {
            var s = v / 255;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Ratio((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double WorstContrast(string foreground, string[] layers, bool dark)
    {
        var wallpaper = Rgb(Value("wallpaper", dark));
        var grounds = new[] { wallpaper, Over(Value("wallpaper-glow", dark), wallpaper) };
        var worst = double.PositiveInfinity;
        foreach (var ground in grounds)
        {
            var surface = ground;
            foreach (var layer in layers) surface = Over(Value(layer, dark), surface);
            var ink = Over(Value(foreground, dark), surface);
            worst = Math.Min(worst, Ratio(ink, surface));
        }
        return worst;
    }
}
