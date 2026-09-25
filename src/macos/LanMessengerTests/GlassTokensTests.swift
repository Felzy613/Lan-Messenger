import XCTest
@testable import LanMessenger

/// Guards the generated design tokens. The spot values prove the generator read
/// tokens.json the way the design system means it; the contrast floors stop a
/// later "tune" of one colour from quietly taking text below what people can
/// read. A surface is the glass composited over the wallpaper and over the
/// wallpaper's green glow, whichever is worse, because that is what a
/// translucent surface actually sits on. Mirrors GlassTokensTests.cs.
final class GlassTokensTests: XCTestCase {

    // MARK: - Spot values

    func testSpotValuesMatchTokensJSON() {
        XCTAssertEqual(GlassTokens.Raw.brand, GlassColorPair(light: 0xFF25D366, dark: 0xFF25D366))
        XCTAssertEqual(GlassTokens.Raw.bubbleOut.dark, 0xE0005C4B)
        // {accent-ink} resolves per theme, not once.
        XCTAssertEqual(GlassTokens.Raw.accentInkOut.light, GlassTokens.Raw.accentInk.light)
        XCTAssertEqual(GlassTokens.Raw.glassRegularFallback, GlassTokens.Raw.sidebarGround)
        XCTAssertEqual(GlassTokens.Radius.bubble, 16)
        XCTAssertEqual(GlassTokens.Size.bubbleMax, 420)
        XCTAssertEqual(GlassTokens.Space.s12, 12)
    }

    /// AvatarPalette keeps its own copy so it stays free of SwiftUI. This is what
    /// stops the two drifting apart.
    func testAvatarPaletteIsTheAvatarTokensInOrder() {
        let tokens = GlassTokens.Raw.all
            .filter { $0.name.hasPrefix("avatar-") }
            .map { $0.value.light & 0xFFFFFF }
        XCTAssertEqual(tokens, AvatarPalette.rgb)
    }

    // MARK: - Contrast floors

    private struct Floor {
        let foreground: String
        let surfaces: [[String]]
        let minimum: Double
    }

    private static let floors: [Floor] = [
        Floor(foreground: "ink",
              surfaces: [["bubble-in"], ["bubble-out"], ["glass-regular"], ["glass-thick"]], minimum: 7.0),
        Floor(foreground: "ink-secondary",
              surfaces: [["bubble-in"], ["glass-regular"], ["glass-thick"], ["sidebar-ground"]], minimum: 4.5),
        Floor(foreground: "meta-out", surfaces: [["bubble-out"]], minimum: 4.5),
        Floor(foreground: "accent-ink",
              surfaces: [["bubble-in"], ["glass-regular"], ["glass-thick"]], minimum: 4.5),
        Floor(foreground: "accent-ink-out", surfaces: [["bubble-out"]], minimum: 4.5),
        Floor(foreground: "on-brand", surfaces: [["brand"]], minimum: 4.5),
        Floor(foreground: "on-warning", surfaces: [["warning"]], minimum: 4.5),
        Floor(foreground: "hud-ink", surfaces: [["hud-surface"], ["hud-surface", "hud-button"]], minimum: 7.0),
        Floor(foreground: "hud-ink-secondary", surfaces: [["hud-surface"]], minimum: 4.5),
        Floor(foreground: "hud-signal-view", surfaces: [["hud-surface"]], minimum: 3.0),
        Floor(foreground: "hud-signal-control", surfaces: [["hud-surface"]], minimum: 3.0),
        Floor(foreground: "ink-inverse",
              surfaces: [["danger"]] + GlassTokens.Raw.all.map(\.name).filter { $0.hasPrefix("avatar-") }.map { [$0] },
              minimum: 4.5),
        Floor(foreground: "tick-read", surfaces: [["bubble-out"]], minimum: 3.0),
        Floor(foreground: "focus-ring", surfaces: [["wallpaper"], ["sidebar-ground"]], minimum: 3.0),
        // Transparency off: the opaque fallbacks must still read.
        Floor(foreground: "ink",
              surfaces: [["bubble-in-fallback"], ["bubble-out-fallback"],
                         ["glass-regular-fallback"], ["glass-thick-fallback"]], minimum: 4.5),
        Floor(foreground: "ink-secondary", surfaces: [["bubble-in-fallback"]], minimum: 4.5),
        Floor(foreground: "meta-out", surfaces: [["bubble-out-fallback"]], minimum: 4.5),
        // Washes sit on glass: the Open pill, the typing capsule, the consent warning.
        Floor(foreground: "accent-ink",
              surfaces: [["bubble-in", "accent-wash"], ["glass-regular", "accent-wash"],
                         ["glass-thick", "accent-wash"]], minimum: 4.5),
        Floor(foreground: "warning-ink",
              surfaces: [["glass-regular", "warning-wash"], ["glass-thick", "warning-wash"]], minimum: 4.5),
        // A confirmation's destructive button: glass on a thick sheet, red label.
        Floor(foreground: "danger-ink", surfaces: [["glass-thick", "glass-regular"]], minimum: 4.5),
    ]

    func testEveryPairingMeetsItsContrastFloor() throws {
        for floor in Self.floors {
            for surface in floor.surfaces {
                for dark in [false, true] {
                    let ratio = try Self.worstContrast(floor.foreground, on: surface, dark: dark)
                    XCTAssertGreaterThanOrEqual(
                        ratio, floor.minimum,
                        "\(floor.foreground) on \(surface.joined(separator: " over ")) "
                            + "(\(dark ? "dark" : "light")) is \(String(format: "%.2f", ratio)):1")
                }
            }
        }
    }

    /// The arithmetic has to be right for the floors to mean anything.
    func testContrastMatchesWCAGReferencePoints() {
        XCTAssertEqual(Self.ratio(Self.rgb(0xFF000000), Self.rgb(0xFFFFFFFF)), 21, accuracy: 0.001)
        XCTAssertEqual(Self.ratio(Self.rgb(0xFF777777), Self.rgb(0xFFFFFFFF)), 4.48, accuracy: 0.01)
    }

    // MARK: - WCAG 2.x

    private typealias RGB = (r: Double, g: Double, b: Double)

    private static func value(_ name: String, dark: Bool) throws -> UInt32 {
        let pair = try XCTUnwrap(GlassTokens.Raw.all.first(where: { $0.name == name })?.value,
                                 "no token named \(name)")
        return dark ? pair.dark : pair.light
    }

    private static func rgb(_ argb: UInt32) -> RGB {
        (Double((argb >> 16) & 0xFF), Double((argb >> 8) & 0xFF), Double(argb & 0xFF))
    }

    private static func over(_ top: UInt32, _ bottom: RGB) -> RGB {
        let alpha = Double((top >> 24) & 0xFF) / 255
        let t = rgb(top)
        return (t.r * alpha + bottom.r * (1 - alpha),
                t.g * alpha + bottom.g * (1 - alpha),
                t.b * alpha + bottom.b * (1 - alpha))
    }

    private static func luminance(_ c: RGB) -> Double {
        func channel(_ v: Double) -> Double {
            let s = v / 255
            return s <= 0.04045 ? s / 12.92 : pow((s + 0.055) / 1.055, 2.4)
        }
        return 0.2126 * channel(c.r) + 0.7152 * channel(c.g) + 0.0722 * channel(c.b)
    }

    private static func ratio(_ a: RGB, _ b: RGB) -> Double {
        let (la, lb) = (luminance(a), luminance(b))
        return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)
    }

    private static func worstContrast(_ foreground: String, on layers: [String], dark: Bool) throws -> Double {
        let wallpaper = rgb(try value("wallpaper", dark: dark))
        let grounds = [wallpaper, over(try value("wallpaper-glow", dark: dark), wallpaper)]
        var worst = Double.infinity
        for ground in grounds {
            var surface = ground
            for layer in layers { surface = over(try value(layer, dark: dark), surface) }
            let ink = over(try value(foreground, dark: dark), surface)
            worst = min(worst, ratio(ink, surface))
        }
        return worst
    }
}
