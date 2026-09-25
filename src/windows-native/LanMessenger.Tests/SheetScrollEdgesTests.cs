using LanMessenger.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation;
using Windows.UI;

namespace LanMessenger.Tests;

// The colour arithmetic behind the Settings sheet's soft scroll edges. A fade
// only disappears into the sheet if it ends in exactly the colour the sheet
// paints at that height, so these pin the pieces that colour is built from:
// compositing, reading a gradient at a position, and the fade's own stops.
// The XAML half (finding the sheet's parts, reading its live brushes) needs a
// running window and is not reachable from MSTest.
[TestClass]
public class SheetScrollEdgesTests
{
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    [TestMethod]
    public void OverIsTheGroundUnderNothingAndTheTopWhenOpaque()
    {
        var ground = GlassTokens.GlassRegularFallbackDark;
        Assert.AreEqual(ground, SheetEdgeColors.Over(Color.FromArgb(0, 255, 255, 255), ground));
        Assert.AreEqual(White, SheetEdgeColors.Over(White, ground));
    }

    [TestMethod]
    public void OverBlendsEachChannelByTheTopsAlpha()
    {
        // 20% white over black is 51 in every channel, and the result is opaque.
        Assert.AreEqual(Color.FromArgb(255, 51, 51, 51), SheetEdgeColors.Over(Color.FromArgb(51, 255, 255, 255), Black));
        // 60% of (100, 200, 0) over (0, 100, 250): 60, 160, 100.
        Assert.AreEqual(Color.FromArgb(255, 60, 160, 100),
            SheetEdgeColors.Over(Color.FromArgb(153, 100, 200, 0), Color.FromArgb(255, 0, 100, 250)));
    }

    // Windows.Foundation.Point holds single-precision floats (0.3 comes back
    // as 0.30000001…), hence the tolerance.
    private const double PointPrecision = 1e-6;

    [TestMethod]
    public void AxisPositionMeasuresAlongTheGradientInItsOwnUnits()
    {
        // The sheen's axis: top to bottom of its box, in relative units.
        Assert.AreEqual(0.3, SheetEdgeColors.AxisPosition(new Point(0, 0), new Point(0, 1), new Point(0.5, 0.3)), PointPrecision);
        // An absolute axis 200 tall: 50 down is a quarter of the way, whatever x is.
        Assert.AreEqual(0.25, SheetEdgeColors.AxisPosition(new Point(0, 0), new Point(0, 200), new Point(10, 50)), PointPrecision);
        // A reversed axis runs the other way.
        Assert.AreEqual(0.75, SheetEdgeColors.AxisPosition(new Point(0, 1), new Point(0, 0), new Point(0, 0.25)), PointPrecision);
        // A degenerate axis is all start.
        Assert.AreEqual(0.0, SheetEdgeColors.AxisPosition(new Point(0.5, 0.5), new Point(0.5, 0.5), new Point(0, 1)), PointPrecision);
    }

    [TestMethod]
    public void SampleHoldsTheEndStopsBeyondThemAndInterpolatesBetween()
    {
        // GlassSheenBrush's shape: the sheen at the top, gone by 55%.
        var sheen = GlassTokens.GlassSheenDark;
        var gone = Color.FromArgb(0, sheen.R, sheen.G, sheen.B);
        List<(double Offset, Color Color)> stops = [(0, sheen), (0.55, gone)];

        Assert.AreEqual(sheen, SheetEdgeColors.Sample(stops, -0.1));
        Assert.AreEqual(sheen, SheetEdgeColors.Sample(stops, 0));
        Assert.AreEqual(gone, SheetEdgeColors.Sample(stops, 0.55));
        Assert.AreEqual(gone, SheetEdgeColors.Sample(stops, 0.9));
        // Halfway down the ramp, half the alpha (15 / 2 = 7.5, away from zero),
        // and the RGB never leaves the stops' own.
        Assert.AreEqual(Color.FromArgb(8, 255, 255, 255), SheetEdgeColors.Sample(stops, 0.275));
    }

    [TestMethod]
    public void SampleReadsStopsInOffsetOrder()
    {
        List<(double Offset, Color Color)> stops = [(1, White), (0, Black)];
        Assert.AreEqual(Color.FromArgb(255, 128, 128, 128), SheetEdgeColors.Sample(stops, 0.5));
    }

    [TestMethod]
    public void AFadeRunsFromItsEdgeColourToThatColourTransparent()
    {
        var edge = GlassTokens.GlassRegularFallbackLight;
        var stops = SheetEdgeColors.FadeStops(edge);

        Assert.AreEqual(0.0, stops[0].Offset);
        Assert.AreEqual(edge, stops[0].Color);
        Assert.AreEqual(1.0, stops[^1].Offset);
        // Transparent in the edge's own RGB, never #00FFFFFF: a stop at plain
        // Transparent drags the middle of the fade through grey.
        Assert.AreEqual(Color.FromArgb(0, edge.R, edge.G, edge.B), stops[^1].Color);
        for (var i = 1; i < stops.Count; i++)
        {
            Assert.IsTrue(stops[i].Offset > stops[i - 1].Offset, "offsets ascend");
            Assert.IsTrue(stops[i].Color.A < stops[i - 1].Color.A, "alpha falls all the way");
            Assert.AreEqual((edge.R, edge.G, edge.B), (stops[i].Color.R, stops[i].Color.G, stops[i].Color.B));
        }
    }

    // The scrolling sheet's ground has to be opaque: the edges switch themselves
    // off on a translucent one, leaving Settings with the hard, sliced edges.
    [TestMethod]
    public void TheScrollingSheetsGroundIsOpaqueInBothThemes()
    {
        Assert.AreEqual((byte)255, GlassTokens.GlassRegularFallbackLight.A);
        Assert.AreEqual((byte)255, GlassTokens.GlassRegularFallbackDark.A);
    }

    // Where the sheen has run out (55% of the sheet's height, as generated),
    // the edge is the bare ground; above it, the ground shows through the sheen.
    // Settings' bottom edge sits well below 55%, its top edge well above.
    [TestMethod]
    public void TheSheenReachesTheTopEdgeButNotTheBottom()
    {
        foreach (var (ground, sheen) in new[]
                 {
                     (GlassTokens.GlassRegularFallbackLight, GlassTokens.GlassSheenLight),
                     (GlassTokens.GlassRegularFallbackDark, GlassTokens.GlassSheenDark),
                 })
        {
            List<(double Offset, Color Color)> stops = [(0, sheen), (0.55, Color.FromArgb(0, sheen.R, sheen.G, sheen.B))];
            // A 630-high sheet at the default window size: the scroll area runs
            // from 54 (under the title) to 558 (above Done).
            var top = SheetEdgeColors.Over(SheetEdgeColors.Sample(stops, 54.0 / 630), ground);
            var bottom = SheetEdgeColors.Over(SheetEdgeColors.Sample(stops, 558.0 / 630), ground);

            Assert.AreEqual(ground, bottom);
            Assert.IsTrue(top.R >= ground.R && top.G >= ground.G && top.B >= ground.B, "the sheen only lightens");
            Assert.AreNotEqual(ground, top);
        }
    }
}
