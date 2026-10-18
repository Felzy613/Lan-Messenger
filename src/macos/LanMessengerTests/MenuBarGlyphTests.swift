import XCTest
import AppKit
@testable import LanMessenger

// Cover for "I have an unread message and no way of knowing unless I open the
// menu". The red dot was drawn as a SwiftUI overlay on an SF Symbol, and a
// template image is flat ink, so it never arrived in the menu bar as a dot.
// The unread glyph is now a non-template image that paints itself; these tests
// render it under the appearances a menu bar actually has and look at pixels.
final class MenuBarGlyphTests: XCTestCase {

    private let scale: CGFloat = 4

    /// Draws `image` under `appearance` the way a status item button would.
    private func render(_ image: NSImage, appearance name: NSAppearance.Name) throws -> NSBitmapImageRep {
        let rep = try XCTUnwrap(NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: Int(image.size.width * scale), pixelsHigh: Int(image.size.height * scale),
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0))
        rep.size = image.size
        let context = try XCTUnwrap(NSGraphicsContext(bitmapImageRep: rep))
        let appearance = try XCTUnwrap(NSAppearance(named: name))

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        appearance.performAsCurrentDrawingAppearance {
            image.draw(in: NSRect(origin: .zero, size: image.size))
        }
        NSGraphicsContext.restoreGraphicsState()
        return rep
    }

    /// The pixel at a point given in image coordinates (origin bottom-left).
    private func pixel(_ rep: NSBitmapImageRep, at point: NSPoint) throws -> NSColor {
        let x = Int(point.x * scale)
        let y = Int((rep.size.height - point.y) * scale)
        let color = try XCTUnwrap(rep.colorAt(x: x, y: y))
        return try XCTUnwrap(color.usingColorSpace(.sRGB))
    }

    private func luminance(_ c: NSColor) -> CGFloat {
        0.2126 * c.redComponent + 0.7152 * c.greenComponent + 0.0722 * c.blueComponent
    }

    private let appearances: [NSAppearance.Name] = [.aqua, .darkAqua, .vibrantLight, .vibrantDark]

    // The dot must not make the status item taller, and the one extra point of
    // width is deliberate (see MenuBarGlyph.unreadSize: it cancels AppKit laying
    // out a non-template image a point narrower, which would otherwise shift the
    // neighbouring menu bar items every time a message arrived).
    func testTheUnreadCanvasOnlyDiffersByTheCompensatingPoint() {
        XCTAssertEqual(MenuBarGlyph.read.size, MenuBarGlyph.size)
        XCTAssertEqual(MenuBarGlyph.unread.size, MenuBarGlyph.unreadSize)
        XCTAssertEqual(MenuBarGlyph.unreadSize.height, MenuBarGlyph.size.height)
        XCTAssertEqual(MenuBarGlyph.unreadSize.width, MenuBarGlyph.size.width + 1)
    }

    // Only a non-template image can carry colour into the menu bar; the quiet
    // state keeps the template so it adapts to the bar with no help from us.
    func testOnlyTheUnreadGlyphIsInColour() {
        XCTAssertTrue(MenuBarGlyph.image(unread: false).isTemplate)
        XCTAssertFalse(MenuBarGlyph.image(unread: true).isTemplate)
    }

    func testTheDotLiesInsideTheCanvas() {
        let size = MenuBarGlyph.unreadSize
        let c = MenuBarGlyph.dotCenter(in: size)
        let r = MenuBarGlyph.dotDiameter / 2
        XCTAssertGreaterThanOrEqual(c.x - r, 0)
        XCTAssertGreaterThanOrEqual(c.y - r, 0)
        XCTAssertLessThanOrEqual(c.x + r, size.width)
        XCTAssertLessThanOrEqual(c.y + r, size.height)
    }

    // The dot is red whichever appearance the bar is drawing in.
    func testTheDotIsRedInEveryAppearance() throws {
        let centre = MenuBarGlyph.dotCenter(in: MenuBarGlyph.unreadSize)
        for name in appearances {
            let rep = try render(MenuBarGlyph.unread, appearance: name)
            let dot = try pixel(rep, at: centre)
            XCTAssertGreaterThan(dot.alphaComponent, 0.95, "\(name.rawValue)")
            XCTAssertGreaterThan(dot.redComponent, 0.7, "\(name.rawValue)")
            XCTAssertLessThan(dot.greenComponent, 0.3, "\(name.rawValue)")
            XCTAssertLessThan(dot.blueComponent, 0.3, "\(name.rawValue)")
        }
    }

    // The glyph follows the menu bar, not a colour chosen once: light ink on a
    // dark bar, dark ink on a light one. A glyph fixed to either goes invisible
    // on the other, which is worse than having no dot.
    func testTheGlyphFollowsTheAppearance() throws {
        let middle = NSPoint(x: MenuBarGlyph.unreadSize.width / 2, y: MenuBarGlyph.unreadSize.height / 2)
        for name in [NSAppearance.Name.darkAqua, .vibrantDark] {
            let glyph = try pixel(try render(MenuBarGlyph.unread, appearance: name), at: middle)
            XCTAssertGreaterThan(glyph.alphaComponent, 0.5, "\(name.rawValue)")
            XCTAssertGreaterThan(luminance(glyph), 0.6, "\(name.rawValue) should draw light ink")
        }
        for name in [NSAppearance.Name.aqua, .vibrantLight] {
            let glyph = try pixel(try render(MenuBarGlyph.unread, appearance: name), at: middle)
            XCTAssertGreaterThan(glyph.alphaComponent, 0.5, "\(name.rawValue)")
            XCTAssertLessThan(luminance(glyph), 0.4, "\(name.rawValue) should draw dark ink")
        }
    }

    // The dot is ringed by a clear gap, so it reads as a badge on the bubble
    // rather than a red smear on a white one.
    func testTheDotIsSeparatedFromTheGlyphByAClearGap() throws {
        let centre = MenuBarGlyph.dotCenter(in: MenuBarGlyph.unreadSize)
        let inset = (MenuBarGlyph.dotDiameter / 2 + MenuBarGlyph.dotGap / 2) / 2.0.squareRoot()
        let ring = NSPoint(x: centre.x - inset, y: centre.y - inset)
        let rep = try render(MenuBarGlyph.unread, appearance: .darkAqua)
        XCTAssertLessThan(try pixel(rep, at: ring).alphaComponent, 0.1)
    }
}
