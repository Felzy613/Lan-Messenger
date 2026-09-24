import SwiftUI

/// The app's colours. Every value comes from `GlassTokens`, which
/// scripts/design/gen_tokens.py generates from design/liquid-glass/tokens.json;
/// nothing here is a literal. The scheme-taking functions stay for call sites
/// that already hold a `ColorScheme`; the rest follow the view's appearance.
enum Theme {
    static let accent = GlassTokens.brand

    static func sidebarBackground(_ scheme: ColorScheme) -> Color {
        GlassTokens.Raw.sidebarGround.color(scheme)
    }

    static func chatBackground(_ scheme: ColorScheme) -> Color {
        GlassTokens.Raw.wallpaper.color(scheme)
    }

    /// Tinted glass: 90% opaque, or the opaque WhatsApp colour with Reduce
    /// Transparency on. Never a material — only the wallpaper is behind a
    /// bubble, and a blur per bubble costs frames nobody can see.
    static func outgoingBubble(_ scheme: ColorScheme, reduceTransparency: Bool = false) -> Color {
        (reduceTransparency ? GlassTokens.Raw.bubbleOutFallback : GlassTokens.Raw.bubbleOut).color(scheme)
    }

    /// 86% opaque; opaque with Reduce Transparency on.
    static func incomingBubble(_ scheme: ColorScheme, reduceTransparency: Bool = false) -> Color {
        (reduceTransparency ? GlassTokens.Raw.bubbleInFallback : GlassTokens.Raw.bubbleIn).color(scheme)
    }

    static let ink = GlassTokens.ink
    static let inkSecondary = GlassTokens.inkSecondary
    /// Meta text (time, "edited", ticks) inside an outgoing bubble, which is
    /// itself green: ink-secondary fails on it.
    static let metaOut = GlassTokens.metaOut
    static let accentInk = GlassTokens.accentInk
    /// Accent text inside an outgoing bubble.
    static let accentInkOut = GlassTokens.accentInkOut
    static let accentWash = GlassTokens.accentWash
    static let tickRead = GlassTokens.tickRead
    /// Glyphs and numerals on a brand fill. White on brand is 2:1.
    static let onBrand = GlassTokens.onBrand
    static let insetFill = GlassTokens.insetFill
    static let presenceOnline = GlassTokens.presenceOnline
    static let presenceOffline = GlassTokens.presenceOffline
    static let presenceRing = GlassTokens.presenceRing
    static let dangerInk = GlassTokens.dangerInk

    /// The meta colour for a bubble on either side.
    static func meta(incoming: Bool) -> Color { incoming ? inkSecondary : metaOut }
    /// The accent text colour for a bubble on either side.
    static func accentInk(incoming: Bool) -> Color { incoming ? accentInk : accentInkOut }

    private static let avatarPalette: [Color] = AvatarPalette.rgb.map { Color(argb: 0xFF00_0000 | $0) }

    static func avatarColor(for name: String) -> Color {
        avatarPalette[AvatarPalette.index(for: name)]
    }

    static func initials(for name: String) -> String {
        let words = name.split(separator: " ")
        if words.count >= 2 {
            return (String(words[0].prefix(1)) + String(words[1].prefix(1))).uppercased()
        }
        return String(name.prefix(2)).uppercased()
    }

    static func formatTimestamp(_ date: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(date) {
            return date.formatted(.dateTime.hour().minute())
        } else if cal.isDateInYesterday(date) {
            return "Yesterday"
        } else if let days = cal.dateComponents([.day], from: date, to: Date()).day, days < 7 {
            return date.formatted(.dateTime.weekday(.abbreviated))
        } else {
            return date.formatted(.dateTime.day().month(.twoDigits).year(.twoDigits))
        }
    }
}
