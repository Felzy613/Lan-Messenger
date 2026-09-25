using LanMessenger.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LanMessenger.UI;

/// <summary>
/// Soft scroll edges for a sheet whose whole body scrolls (Settings): rows
/// dissolve into the sheet as they pass under its title or come down to its
/// buttons, instead of being cut by the edge of the scroll area. Cut, they
/// read as a rendering fault: "Reset" and "Change…" sliced in half under the
/// title, a group's panel sheared flat above Done. This is the scroll edge
/// effect of the Liquid Glass design, done with what WinUI has.
/// </summary>
/// <remarks>
/// <para>
/// WinUI has no opacity mask, so the rows cannot be faded out themselves.
/// Instead each end of the scroll area gets a gradient in the sheet's own
/// colour, and that only works if the colour is exact: a fade that ends a few
/// levels off draws a line across the sheet at the very edge it was meant to
/// remove. So the colour is read from what the sheet actually paints at that
/// height, its ground with the sheen over it, rather than assumed from the
/// tokens, and the edges stay off, leaving the hard edge, whenever the ground
/// is not an opaque solid colour (GlassScrollingDialogStyle makes it one) or
/// the sheet's parts cannot be found.
/// </para>
/// <para>
/// A fade shows only while content lies beyond its edge, and only as strongly
/// as there is content to hide, so a sheet at rest looks exactly as it would
/// without them. Anything brought into view (keyboard focus, a text caret)
/// lands the same depth clear of the edges instead of half inside a fade.
/// </para>
/// </remarks>
public sealed class SheetScrollEdges
{
    private readonly ScrollViewer _scroller;
    private readonly Shape _top;
    private readonly Shape _bottom;
    private readonly double _depth;
    private readonly DispatcherQueue _queue;

    // High Contrast and the system colours can change while the sheet is open
    // without the element theme changing, and UISettings is what says so. Kept
    // for the life of the sheet, because its event only fires while the
    // instance that subscribed is alive, and unsubscribed on Unloaded so the
    // closed sheet can be collected.
    private readonly UISettings _uiSettings = new();
    private bool _listening;
    private bool _loggedFailure;

    /// <summary>
    /// Gives a sheet's scroll area soft edges. The two shapes are laid over the
    /// top and bottom of the scroll area in XAML, as tall as the fade is deep,
    /// and start collapsed; this fills and shows them once the sheet's colour
    /// is known. Nothing needs to hold the result: the handlers it attaches to
    /// these elements keep it alive exactly as long as they are.
    /// </summary>
    /// <param name="scroller">The sheet body's scroll area.</param>
    /// <param name="content">The scroller's content, which bring-into-view requests pass through.</param>
    /// <param name="top">Laid over the top of the scroll area.</param>
    /// <param name="bottom">Laid over the bottom of the scroll area, the same height.</param>
    public static void Attach(ScrollViewer scroller, FrameworkElement content, Shape top, Shape bottom)
        => _ = new SheetScrollEdges(scroller, content, top, bottom);

    private SheetScrollEdges(ScrollViewer scroller, FrameworkElement content, Shape top, Shape bottom)
    {
        _scroller = scroller;
        _top = top;
        _bottom = bottom;
        _queue = scroller.DispatcherQueue;
        // The fades' own height (a token, set in XAML) is the depth everywhere:
        // how far they reach, how soon they come in, and the margin kept round
        // anything brought into view. NaN (no height set) is not > 0.
        _depth = top.Height > 0 ? top.Height : 0;

        scroller.ViewChanged += (_, _) => UpdateStrength();
        scroller.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => UpdateStrength());
        // The extent can change without the offset (a hint appears, the update
        // notes expand), and ScrollableHeight is settled only after layout.
        content.SizeChanged += (_, _) => _queue.TryEnqueue(UpdateStrength);
        // The sheet's height decides how much sheen lies over each edge.
        scroller.SizeChanged += (_, _) => Repaint();
        // Theme resources are swapped as the theme changes; read them after.
        scroller.ActualThemeChanged += (_, _) => RepaintSoon();
        scroller.Loaded += (_, _) =>
        {
            if (!_listening)
            {
                _uiSettings.ColorValuesChanged += OnColorValuesChanged;
                _listening = true;
            }
            Repaint();
        };
        scroller.Unloaded += (_, _) =>
        {
            if (!_listening) return;
            _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
            _listening = false;
        };
        content.BringIntoViewRequested += KeepClearOfEdges;
    }

    // Raised on a background thread.
    private void OnColorValuesChanged(UISettings sender, object args) => RepaintSoon();

    // Low priority, so XAML has applied the new theme's resources first.
    private void RepaintSoon() => _queue.TryEnqueue(DispatcherQueuePriority.Low, Repaint);

    private void Repaint()
    {
        // Every caller is an event handler, and nothing in one may throw. On
        // any failure the sheet keeps its hard edges, which is how it looked
        // before these existed.
        try
        {
            if (_depth > 0 && TryReadEdgeColors(out var topEdge, out var bottomEdge))
            {
                _top.Fill = FadeBrush(topEdge, opaqueAtTop: true);
                _bottom.Fill = FadeBrush(bottomEdge, opaqueAtTop: false);
                Show(true);
                UpdateStrength();
                return;
            }
        }
        catch (Exception ex)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                LanLogger.Warn("SheetScrollEdges", $"kept hard edges: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Show(false);
    }

    private void Show(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_top.Visibility != visibility) _top.Visibility = visibility;
        if (_bottom.Visibility != visibility) _bottom.Visibility = visibility;
    }

    // Each fade as strong as the content beyond its edge is deep, up to the
    // full depth: at rest at the top, the top one is off and the first header
    // sits in the clear, exactly as without it.
    private void UpdateStrength()
    {
        if (_depth <= 0) return;
        var offset = _scroller.VerticalOffset;
        _top.Opacity = Math.Clamp(offset / _depth, 0, 1);
        _bottom.Opacity = Math.Clamp((_scroller.ScrollableHeight - offset) / _depth, 0, 1);
    }

    // Keyboard focus and the text caret bring their element into view by the
    // least scroll that shows it, which parks it flush against the edge, under
    // a fade. Asking for the element plus the depth either side lands it clear.
    private void KeepClearOfEdges(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        var rect = args.TargetRect;
        // Rect.Empty has negative sizes.
        if (_depth <= 0 || rect.Width < 0 || rect.Height < 0) return;
        args.TargetRect = new Rect(rect.X, rect.Y - _depth, rect.Width, rect.Height + 2 * _depth);
    }

    private bool TryReadEdgeColors(out Color top, out Color bottom)
    {
        top = bottom = default;
        var height = _scroller.ActualHeight;
        if (height <= 0) return false;

        // The parts of GlassDialogStyle the body sits in: the sheet's ground
        // (BackgroundElement's fill), and the sheen laid over all of it.
        if (Ancestor(_scroller, "BackgroundElement") is not Border { Background: SolidColorBrush ground }
            || ground.Color.A != 255 || ground.Opacity < 1)
            return false;
        if (Ancestor(_scroller, "DialogSpace") is not { } space
            || Child(space, "SheenElement") is not Border sheen)
            return false;

        var y = _scroller.TransformToVisual(sheen).TransformPoint(new Point(0, 0)).Y;
        if (SheenAt(sheen, y) is not { } sheenTop || SheenAt(sheen, y + height) is not { } sheenBottom)
            return false;
        top = SheetEdgeColors.Over(sheenTop, ground.Color);
        bottom = SheetEdgeColors.Over(sheenBottom, ground.Color);
        return true;
    }

    // The sheen's colour at height y within it, as drawn: its brush sampled
    // there, with the brush's and the element's opacity. Null for a brush this
    // cannot read, so the caller keeps the hard edge rather than guess.
    private static Color? SheenAt(Border sheen, double y)
    {
        var opacity = sheen.Opacity;
        switch (sheen.Background)
        {
            case null:
                return Color.FromArgb(0, 0, 0, 0);
            case SolidColorBrush solid:
                return SheetEdgeColors.Faded(solid.Color, solid.Opacity * opacity);
            case LinearGradientBrush gradient
                when gradient.Transform is null && gradient.RelativeTransform is null
                     && gradient.SpreadMethod == GradientSpreadMethod.Pad:
                var at = gradient.MappingMode == BrushMappingMode.RelativeToBoundingBox
                    ? new Point(0.5, sheen.ActualHeight > 0 ? y / sheen.ActualHeight : 0)
                    : new Point(sheen.ActualWidth / 2, y);
                var stops = gradient.GradientStops.Select(s => (s.Offset, s.Color)).ToList();
                var position = SheetEdgeColors.AxisPosition(gradient.StartPoint, gradient.EndPoint, at);
                return SheetEdgeColors.Faded(SheetEdgeColors.Sample(stops, position), gradient.Opacity * opacity);
            default:
                return null;
        }
    }

    private static LinearGradientBrush FadeBrush(Color edge, bool opaqueAtTop)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, opaqueAtTop ? 0 : 1),
            EndPoint   = new Point(0, opaqueAtTop ? 1 : 0),
        };
        foreach (var (offset, color) in SheetEdgeColors.FadeStops(edge))
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = color });
        return brush;
    }

    private static FrameworkElement? Ancestor(DependencyObject from, string name)
    {
        for (var node = VisualTreeHelper.GetParent(from); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement element && element.Name == name) return element;
        return null;
    }

    private static FrameworkElement? Child(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
            if (VisualTreeHelper.GetChild(parent, i) is FrameworkElement element && element.Name == name) return element;
        return null;
    }
}

/// <summary>
/// The colour arithmetic behind <see cref="SheetScrollEdges"/>, kept free of
/// XAML objects so the tests can run it.
/// </summary>
public static class SheetEdgeColors
{
    /// <summary>
    /// <paramref name="top"/> (straight alpha) composited over an opaque
    /// colour, as XAML composites a translucent layer: per channel, in sRGB.
    /// </summary>
    public static Color Over(Color top, Color opaqueBottom)
    {
        var a = top.A / 255.0;
        byte Mix(byte over, byte under) => Round(over * a + under * (1 - a));
        return Color.FromArgb(255, Mix(top.R, opaqueBottom.R), Mix(top.G, opaqueBottom.G), Mix(top.B, opaqueBottom.B));
    }

    /// <summary>A colour with its alpha scaled by an opacity.</summary>
    public static Color Faded(Color color, double opacity)
        => Color.FromArgb(Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);

    /// <summary>
    /// Where <paramref name="at"/> falls along a linear gradient's axis, in the
    /// gradient's own units: 0 at the start point, 1 at the end point.
    /// </summary>
    public static double AxisPosition(Point start, Point end, Point at)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        return lengthSquared > 0 ? ((at.X - start.X) * dx + (at.Y - start.Y) * dy) / lengthSquared : 0;
    }

    /// <summary>
    /// A gradient's colour at a position along its axis: the end stops' own
    /// colours beyond them (the Pad spread), and straight-alpha linear between
    /// the two stops around it.
    /// </summary>
    public static Color Sample(IReadOnlyList<(double Offset, Color Color)> stops, double position)
    {
        if (stops.Count == 0) return Color.FromArgb(0, 0, 0, 0);
        var sorted = stops.OrderBy(s => s.Offset).ToList();
        if (position <= sorted[0].Offset) return sorted[0].Color;
        for (var i = 1; i < sorted.Count; i++)
        {
            var (endOffset, end) = sorted[i];
            if (position > endOffset) continue;
            var (startOffset, start) = sorted[i - 1];
            var t = endOffset > startOffset ? (position - startOffset) / (endOffset - startOffset) : 1;
            byte Lerp(byte a0, byte a1) => Round(a0 + (a1 - a0) * t);
            return Color.FromArgb(Lerp(start.A, end.A), Lerp(start.R, end.R), Lerp(start.G, end.G), Lerp(start.B, end.B));
        }
        return sorted[^1].Color;
    }

    /// <summary>
    /// A scroll edge's gradient stops, from <paramref name="edge"/> at the edge
    /// to its transparent twin at the fade's inner end. Eased (one minus
    /// smoothstep) rather than linear, so neither end of the fade shows as a
    /// line; and the far stop keeps the edge's RGB, so the fade never passes
    /// through the grey a stop at Transparent (#00FFFFFF) would drag in.
    /// </summary>
    public static IReadOnlyList<(double Offset, Color Color)> FadeStops(Color edge)
    {
        (double Offset, double Alpha)[] curve = [(0, 1), (0.25, 0.84375), (0.5, 0.5), (0.75, 0.15625), (1, 0)];
        return curve.Select(p => (p.Offset, Faded(edge, p.Alpha))).ToList();
    }

    private static byte Round(double value)
        => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
