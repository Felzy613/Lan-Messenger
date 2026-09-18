import XCTest
import CoreGraphics
@testable import LanMessenger

// Where a viewer's pointer lands on this host's screen.
//
// Everything here arrives from a peer, so the tests lean on hostile input as
// much as on arithmetic. A peer that sends 5.0 where the protocol says [0,1] is
// asking for the cursor to be put somewhere it was never granted access to.
final class RemoteInputGeometryTests: XCTestCase {

    private let display = CGRect(x: 0, y: 0, width: 1920, height: 1080)

    // MARK: - The boundary

    func testOutOfRangeCoordinatesAreClampedNotTrusted() {
        // Not defensive programming — this is the boundary. Off-surface values
        // would otherwise put a click on a display the viewer was never shown.
        XCTAssertEqual(RemoteInputGeometry.clampUnit(5.0), 1.0)
        XCTAssertEqual(RemoteInputGeometry.clampUnit(-3.0), 0.0)
        XCTAssertEqual(RemoteInputGeometry.clampUnit(0.25), 0.25)
    }

    func testNonFiniteCoordinatesBecomeZero() {
        // A decoder hands NaN over without complaint, and NaN * width is NaN,
        // which becomes an unpredictable Int32 on conversion rather than an error.
        XCTAssertEqual(RemoteInputGeometry.clampUnit(.nan), 0)
        XCTAssertEqual(RemoteInputGeometry.clampUnit(.infinity), 0)
        XCTAssertEqual(RemoteInputGeometry.clampUnit(-.infinity), 0)

        let point = RemoteInputGeometry.displayPoint(
            normalizedX: .nan, normalizedY: .infinity, displayBounds: display)
        XCTAssertEqual(point, CGPoint(x: 0, y: 0))
    }

    func testAHostilePositionCannotEscapeTheSharedDisplay() {
        let point = RemoteInputGeometry.displayPoint(
            normalizedX: 12.0, normalizedY: -9.0, displayBounds: display)
        XCTAssertTrue(display.contains(point) || point.x == display.maxX || point.y == display.minY,
                      "\(point) left the display it was granted")
        XCTAssertEqual(point.x, 1920)
        XCTAssertEqual(point.y, 0)
    }

    // MARK: - The mapping

    func testTheCentreMapsToTheCentre() {
        let point = RemoteInputGeometry.displayPoint(
            normalizedX: 0.5, normalizedY: 0.5, displayBounds: display)
        XCTAssertEqual(point, CGPoint(x: 960, y: 540))
    }

    func testTheOriginIsTopLeftBecauseCGEventSaysSo() {
        // CGDisplayBounds is a top-left origin space; NSScreen.frame is not.
        // A position derived from NSScreen is mirrored vertically, which looks
        // almost right on a centred click and completely wrong at the edges.
        XCTAssertEqual(RemoteInputGeometry.displayPoint(
            normalizedX: 0, normalizedY: 0, displayBounds: display).y, 0)
        XCTAssertEqual(RemoteInputGeometry.displayPoint(
            normalizedX: 0, normalizedY: 1, displayBounds: display).y, 1080)
    }

    func testASecondaryDisplayIsResolvedAgainstItsOwnOrigin() {
        // Global display space: a second monitor does not start at zero, and a
        // position computed without its origin lands on the primary instead.
        let secondary = CGRect(x: 1920, y: -200, width: 1280, height: 800)
        let point = RemoteInputGeometry.displayPoint(
            normalizedX: 0.5, normalizedY: 0.5, displayBounds: secondary)
        XCTAssertEqual(point, CGPoint(x: 2560, y: 200))
    }

    func testSurfacePixelsGoThroughTheNormalizedValueNotACachedScale() {
        // The capture may be downscaled from the display, so points and pixels
        // are not interchangeable.
        let pixel = RemoteInputGeometry.surfacePixel(
            normalizedX: 0.5, normalizedY: 0.25, surface: CGSize(width: 1280, height: 720))
        XCTAssertEqual(pixel, CGPoint(x: 640, y: 180))

        XCTAssertEqual(RemoteInputGeometry.surfacePixel(
            normalizedX: 1.0, normalizedY: 1.0, surface: CGSize(width: 1280, height: 720)),
            CGPoint(x: 1280, y: 720))
    }

    // MARK: - Scroll

    func testAbsurdScrollDeltasAreCapped() {
        // A single wheel event of 100,000 lines is not a scroll, it is a way to
        // make a host's document jump somewhere unrecoverable in one frame.
        XCTAssertEqual(RemoteInputGeometry.clampScroll(100_000), 120)
        XCTAssertEqual(RemoteInputGeometry.clampScroll(-100_000), -120)
        XCTAssertEqual(RemoteInputGeometry.clampScroll(3), 3)
        XCTAssertEqual(RemoteInputGeometry.clampScroll(.nan), 0)
    }
}
