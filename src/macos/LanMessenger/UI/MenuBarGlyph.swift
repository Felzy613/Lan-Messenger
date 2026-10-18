import AppKit
import SwiftUI

// The menu bar glyph, with the unread dot composited into the image itself.
//
// MenuBarExtra hands its label's NSImage straight to the status item's button
// (the button has no subviews, and the image is the very object the label
// supplied). An SF Symbol is a template image, which AppKit draws as a single
// flat colour — so a red `Circle()` laid over the symbol in SwiftUI never
// arrives as red: it is flattened to the same ink as the glyph and vanishes
// into it. The only way to get colour into the menu bar is a NON-template image,
// and a non-template image gets no free light/dark adaptation, so this one draws
// itself.
//
// The drawing handler runs each time the button paints, under the *menu bar's*
// appearance — not NSApp's. They differ: with a dark wallpaper the bar is
// vibrant-dark while the app is still Aqua, which is why the glyph is tinted
// from `NSColor.labelColor` at draw time rather than picked once from
// `NSApp.effectiveAppearance`. The dot is baked in rather than attached to the
// button as a subview because a status item is replicated onto every display's
// menu bar and the copies draw the image, not the button's children.
enum MenuBarGlyph {
    static let symbolName = "message.fill"

    /// Diameter of the red dot, in points.
    static let dotDiameter: CGFloat = 8
    /// Clear gap cut around the dot so it separates from the glyph beneath it.
    static let dotGap: CGFloat = 1.5

    private static let symbol: NSImage? =
        NSImage(systemSymbolName: symbolName, accessibilityDescription: nil)

    /// The glyph's own size, which is the read image's.
    static let size: NSSize = symbol?.size ?? NSSize(width: 17, height: 15)

    /// The unread canvas is one point wider than the glyph. A status item is as
    /// wide as its image, and AppKit lays a non-template image out one point
    /// narrower than a template of the same size (measured: 34pt vs 33pt for this
    /// glyph), so at equal sizes every item to the left would shift by a point
    /// each time a message arrived. The extra point restores the 34 and, centred,
    /// leaves the glyph exactly where the template puts it. The dot itself sits
    /// inside the canvas, so the height is unchanged.
    static let unreadSize = NSSize(width: size.width + 1, height: size.height)

    /// The dot's centre, tucked into the top-right corner of the canvas.
    static func dotCenter(in size: NSSize) -> NSPoint {
        NSPoint(x: size.width - dotDiameter / 2, y: size.height - dotDiameter / 2)
    }

    static let read: NSImage = {
        let image = symbol ?? NSImage(size: size)
        image.isTemplate = true
        return image
    }()

    static let unread: NSImage = {
        let image = NSImage(size: unreadSize, flipped: false) { rect in
            drawGlyph(in: rect)
            drawDot(in: rect)
            return true
        }
        image.isTemplate = false
        return image
    }()

    static func image(unread hasUnread: Bool) -> NSImage {
        hasUnread ? unread : read
    }

    private static func drawGlyph(in rect: NSRect) {
        guard let symbol else { return }
        let glyph = NSRect(x: (rect.width - symbol.size.width) / 2,
                           y: (rect.height - symbol.size.height) / 2,
                           width: symbol.size.width,
                           height: symbol.size.height)
        symbol.draw(in: glyph)
        // A template draws as flat ink; recolour exactly the pixels it produced.
        NSColor.labelColor.set()
        glyph.fill(using: .sourceIn)
    }

    private static func drawDot(in rect: NSRect) {
        let centre = dotCenter(in: rect.size)
        let gap = dotDiameter + dotGap * 2
        let cut = NSRect(x: centre.x - gap / 2, y: centre.y - gap / 2, width: gap, height: gap)
        NSGraphicsContext.current?.saveGraphicsState()
        NSGraphicsContext.current?.compositingOperation = .clear
        NSBezierPath(ovalIn: cut).fill()
        NSGraphicsContext.current?.restoreGraphicsState()

        let dot = NSRect(x: centre.x - dotDiameter / 2, y: centre.y - dotDiameter / 2,
                         width: dotDiameter, height: dotDiameter)
        NSColor(GlassTokens.danger).setFill()
        NSBezierPath(ovalIn: dot).fill()
    }
}
