using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Where a viewer's pointer lands on this host's screen. Mirror of the macOS
// RemoteInputGeometryTests, plus the SendInput absolute conversion macOS does
// not need.
//
// Everything here arrives from a peer, so the tests lean on hostile input as
// much as on arithmetic.
[TestClass]
public class RemoteInputGeometryTests
{
    private static readonly VirtualScreen Single = new(0, 0, 1920, 1080);

    // ---- The boundary ------------------------------------------------------

    [TestMethod]
    public void OutOfRangeCoordinatesAreClampedNotTrusted()
    {
        Assert.AreEqual(1.0, RemoteInputGeometry.ClampUnit(5.0));
        Assert.AreEqual(0.0, RemoteInputGeometry.ClampUnit(-3.0));
        Assert.AreEqual(0.25, RemoteInputGeometry.ClampUnit(0.25));
    }

    [TestMethod]
    public void NonFiniteCoordinatesBecomeZero()
    {
        Assert.AreEqual(0.0, RemoteInputGeometry.ClampUnit(double.NaN));
        Assert.AreEqual(0.0, RemoteInputGeometry.ClampUnit(double.PositiveInfinity));
        Assert.AreEqual(0.0, RemoteInputGeometry.ClampUnit(double.NegativeInfinity));
    }

    // ---- The off-by-one that costs the rightmost column ---------------------

    [TestMethod]
    public void TheRightmostColumnAndBottomRowAreReachable()
    {
        // The divisor is width - 1, not width. Dividing by the width leaves a
        // one-pixel band the cursor can never enter, which nobody notices until
        // they try to hit a close button.
        var (dx, dy) = RemoteInputGeometry.ToAbsolute(1919, 1079, Single);
        Assert.AreEqual(65535, dx, "the rightmost column must be reachable");
        Assert.AreEqual(65535, dy, "the bottom row must be reachable");
    }

    [TestMethod]
    public void TheOriginMapsToZero()
    {
        var (dx, dy) = RemoteInputGeometry.ToAbsolute(0, 0, Single);
        Assert.AreEqual(0, dx);
        Assert.AreEqual(0, dy);
    }

    [TestMethod]
    public void TheVirtualScreenOriginIsSubtracted()
    {
        // A second monitor to the left of the primary gives SM_XVIRTUALSCREEN a
        // negative origin. Forgetting to subtract it puts every click on the
        // wrong display.
        var spanning = new VirtualScreen(-1920, -200, 3840, 1280);

        var (leftDx, _) = RemoteInputGeometry.ToAbsolute(-1920, -200, spanning);
        Assert.AreEqual(0, leftDx, "the leftmost pixel of a negative-origin screen is 0");

        var (rightDx, bottomDy) = RemoteInputGeometry.ToAbsolute(1919, 1079, spanning);
        Assert.AreEqual(65535, rightDx);
        Assert.AreEqual(65535, bottomDy);
    }

    [TestMethod]
    public void APixelOutsideTheVirtualScreenIsClampedIntoIt()
    {
        var (dx, dy) = RemoteInputGeometry.ToAbsolute(99999, -99999, Single);
        Assert.AreEqual(65535, dx);
        Assert.AreEqual(0, dy);
    }

    [TestMethod]
    public void ADegenerateScreenDoesNotDivideByZero()
    {
        var (dx, dy) = RemoteInputGeometry.ToAbsolute(0, 0, new VirtualScreen(0, 0, 1, 1));
        Assert.AreEqual(0, dx);
        Assert.AreEqual(0, dy);
    }

    // ---- The mapping -------------------------------------------------------

    [TestMethod]
    public void TheCentreMapsToTheCentre()
    {
        var (x, y) = RemoteInputGeometry.ToDisplayPixel(0.5, 0.5, 0, 0, 1920, 1080);
        Assert.AreEqual(960, x);
        Assert.AreEqual(540, y);
    }

    [TestMethod]
    public void ASecondaryDisplayIsResolvedAgainstItsOwnOrigin()
    {
        var (x, y) = RemoteInputGeometry.ToDisplayPixel(0.5, 0.5, 1920, -200, 1280, 800);
        Assert.AreEqual(2560, x);
        Assert.AreEqual(200, y);
    }

    [TestMethod]
    public void AHostilePositionCannotEscapeTheSharedDisplay()
    {
        var (x, y) = RemoteInputGeometry.ToDisplayPixel(12.0, -9.0, 0, 0, 1920, 1080);
        Assert.AreEqual(1920, x);
        Assert.AreEqual(0, y);
    }

    // ---- Scroll ------------------------------------------------------------

    [TestMethod]
    public void AbsurdScrollDeltasAreCapped()
    {
        Assert.AreEqual(120, RemoteInputGeometry.ClampScroll(100_000));
        Assert.AreEqual(-120, RemoteInputGeometry.ClampScroll(-100_000));
        Assert.AreEqual(3, RemoteInputGeometry.ClampScroll(3));
        Assert.AreEqual(0, RemoteInputGeometry.ClampScroll(double.NaN));
    }
}
