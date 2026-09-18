namespace LanMessenger.Core.Networking.Media;

// Turning a viewer's pointer position into a place on this host's screen.
// Mirror of the macOS RemoteInputGeometry.swift, with the extra step Windows
// needs.
//
// PROTOCOL.md puts pointer positions on the wire as normalized floats in [0,1]
// relative to the shared video surface, so a viewer never needs to know anything
// about the host's displays and a host never has to trust a viewer's arithmetic.
//
// Windows then needs a second conversion that macOS does not: SendInput's
// absolute mode does not take pixels, it takes a 0..65535 range across the whole
// virtual screen. Two things about that conversion are easy to get wrong and
// both are silent:
//
//   * The divisor is width - 1, not width. Dividing by the width makes the
//     rightmost column and bottom row unreachable — a one-pixel band the cursor
//     can never enter, which nobody notices until they try to hit a close
//     button.
//   * The virtual screen does not start at zero. A second monitor to the left of
//     the primary gives SM_XVIRTUALSCREEN a negative origin, and forgetting to
//     subtract it puts every click on the wrong display.

/// The bounding box of every display, as SendInput measures it:
/// SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN.
public readonly record struct VirtualScreen(int X, int Y, int Width, int Height);

public static class RemoteInputGeometry
{
    /// Coerces a coordinate from the wire into the unit range.
    ///
    /// NaN and infinity are handled deliberately: a decoder hands those over
    /// without complaint, and the usual arithmetic turns them into an
    /// unpredictable int rather than an error.
    public static double ClampUnit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        return Math.Min(Math.Max(value, 0), 1);
    }

    /// Resolves a normalized position to a pixel inside one display's bounds.
    public static (int X, int Y) ToDisplayPixel(
        double normalizedX, double normalizedY, int displayX, int displayY,
        int displayWidth, int displayHeight)
    {
        var x = displayX + (int)(ClampUnit(normalizedX) * displayWidth);
        var y = displayY + (int)(ClampUnit(normalizedY) * displayHeight);
        return (x, y);
    }

    /// Converts a virtual-screen pixel to the 0..65535 range SendInput's
    /// MOUSEEVENTF_ABSOLUTE expects.
    ///
    /// The result is clamped, because a pixel outside the virtual screen would
    /// otherwise produce a value outside the range and land somewhere the driver
    /// picks. A degenerate one-pixel screen divides by zero without the guard.
    public static (int Dx, int Dy) ToAbsolute(int pixelX, int pixelY, VirtualScreen screen)
    {
        var spanX = Math.Max(screen.Width - 1, 1);
        var spanY = Math.Max(screen.Height - 1, 1);

        var dx = (long)(pixelX - screen.X) * 65535 / spanX;
        var dy = (long)(pixelY - screen.Y) * 65535 / spanY;

        return ((int)Math.Clamp(dx, 0, 65535), (int)Math.Clamp(dy, 0, 65535));
    }

    /// Scroll deltas arrive as lines, and a peer can send absurd ones. A single
    /// wheel event of 100,000 lines is not a scroll, it is a way to make a host's
    /// document jump somewhere unrecoverable in one frame.
    public const int MaxScrollLines = 120;

    public static int ClampScroll(double lines)
    {
        if (double.IsNaN(lines) || double.IsInfinity(lines)) return 0;
        return (int)Math.Min(Math.Max(lines, -MaxScrollLines), MaxScrollLines);
    }
}
