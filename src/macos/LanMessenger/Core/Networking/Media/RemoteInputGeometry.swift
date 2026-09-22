import Foundation
import CoreGraphics

// Turning a viewer's pointer position into a place on this host's screen.
//
// PROTOCOL.md puts pointer positions on the wire as **normalized floats in
// [0,1], relative to the shared video surface** — not the viewer's window. The
// host resolves them itself, which is what keeps display scaling, Retina backing
// scale and multi-monitor layout entirely out of the wire format. A viewer does
// not need to know anything about the host's displays, and a host does not have
// to trust a viewer's arithmetic.
//
// That last part is the reason this file is not two lines of multiplication.
// Everything arriving here was typed by a peer, and a peer that sends 5.0 where
// the protocol says [0,1] is asking for the cursor to be put somewhere it was
// never granted access to — another display, or off-screen entirely, where a
// click lands on something the host cannot see. Clamping is not defensive
// programming, it is the boundary.

enum RemoteInputGeometry {

    /// Coerces a coordinate from the wire into the unit range.
    ///
    /// NaN and infinity are included deliberately: a JSON or binary decoder will
    /// hand those over without complaint, and `Double.nan * width` is NaN, which
    /// becomes an unpredictable `Int32` on conversion rather than an error.
    static func clampUnit(_ value: Double) -> Double {
        guard value.isFinite else { return 0 }
        return min(max(value, 0), 1)
    }

    /// Resolves a normalized position onto a display, in the global, top-left
    /// origin **point** space `CGEvent` speaks.
    ///
    /// `CGDisplayBounds` is the right source and `NSScreen.frame` is not: AppKit
    /// reports a bottom-left origin in a coordinate space whose y axis runs the
    /// other way, so a position derived from it is mirrored vertically — which
    /// looks almost right on a centred click and completely wrong at the edges.
    static func displayPoint(normalizedX: Double,
                             normalizedY: Double,
                             displayBounds: CGRect) -> CGPoint {
        let x = clampUnit(normalizedX)
        let y = clampUnit(normalizedY)
        return CGPoint(x: displayBounds.origin.x + x * displayBounds.width,
                       y: displayBounds.origin.y + y * displayBounds.height)
    }

    /// The same, expressed against the captured surface's pixel size.
    ///
    /// Only needed where something downstream wants pixels rather than points —
    /// the capture may be downscaled from the display (`maxLongEdge`), so the
    /// two are not interchangeable and the conversion must go through the
    /// normalized value rather than through a scale factor somebody cached.
    static func surfacePixel(normalizedX: Double,
                             normalizedY: Double,
                             surface: CGSize) -> CGPoint {
        CGPoint(x: (clampUnit(normalizedX) * surface.width).rounded(.down),
                y: (clampUnit(normalizedY) * surface.height).rounded(.down))
    }

    /// Scroll deltas arrive as lines, and a peer can send absurd ones.
    ///
    /// A single wheel event of 100,000 lines is not a scroll, it is a way to
    /// make a host's document jump somewhere unrecoverable in one frame. The cap
    /// is generous next to any real trackpad flick.
    static let maxScrollLines = 120

    static func clampScroll(_ lines: Double) -> Int32 {
        guard lines.isFinite else { return 0 }
        let bounded = min(max(lines, Double(-maxScrollLines)), Double(maxScrollLines))
        return Int32(bounded)
    }
}
