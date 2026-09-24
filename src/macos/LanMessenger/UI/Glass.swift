import SwiftUI

/// The three thicknesses of glass in the design system: clear for icon-only
/// controls over busy content, regular for chrome holding a line of text,
/// thick for anything read at length.
enum GlassThickness {
    case clear, regular, thick
}

/// Whether this build, on this system, draws Liquid Glass. Layout that only
/// makes sense with glass (a floating header capsule instead of a bar and a
/// divider) branches on this; the surfaces themselves go through
/// `glassSurface`, which makes the same decision.
enum LiquidGlass {
    static var isAvailable: Bool {
        #if compiler(>=6.2)
        if #available(macOS 26, *) { return true }
        #endif
        return false
    }
}

extension View {
    /// One call site for every glass surface. macOS 26 gets real Liquid Glass;
    /// earlier systems get the matching material and the glass edge; Reduce
    /// Transparency gets the opaque fallback token.
    ///
    /// The Liquid Glass branch is also behind `#if compiler(>=6.2)`: the API
    /// only exists in the macOS 26 SDK, and `#available` alone does not stop
    /// an older toolchain from failing to find the symbol.
    func glassSurface<S: InsettableShape>(_ thickness: GlassThickness = .regular,
                                          in shape: S,
                                          tint: Color? = nil) -> some View {
        modifier(GlassSurfaceModifier(thickness: thickness, shape: shape, tint: tint))
    }
}

private struct GlassSurfaceModifier<S: InsettableShape>: ViewModifier {
    let thickness: GlassThickness
    let shape: S
    let tint: Color?

    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    func body(content: Content) -> some View {
        if reduceTransparency {
            content
                .background(fallback, in: shape)
                .overlay(shape.strokeBorder(GlassTokens.glassEdge, lineWidth: 1))
        } else {
            glass(content)
        }
    }

    @ViewBuilder
    private func glass(_ content: Content) -> some View {
        #if compiler(>=6.2)
        if #available(macOS 26, *) {
            content.glassEffect(thickness == .clear ? .clear : .regular.tint(tint), in: shape)
        } else {
            material(content)
        }
        #else
        material(content)
        #endif
    }

    private func material(_ content: Content) -> some View {
        content
            .background(materialStyle, in: shape)
            .overlay(shape.strokeBorder(GlassTokens.glassEdge, lineWidth: 1))
    }

    private var materialStyle: Material {
        switch thickness {
        case .clear: return .ultraThinMaterial
        case .regular: return .regularMaterial
        case .thick: return .thickMaterial
        }
    }

    private var fallback: Color {
        thickness == .thick ? GlassTokens.glassThickFallback : GlassTokens.glassRegularFallback
    }
}

extension View {
    /// A shadow token's layers for the scheme in hand (at most two, which is
    /// what the tokens have). SwiftUI cannot pick a shadow's radius by
    /// appearance on its own, and the dark tokens are larger.
    func glassShadow(light: [GlassShadowLayer], dark: [GlassShadowLayer], scheme: ColorScheme) -> some View {
        modifier(GlassShadowModifier(layers: scheme == .dark ? dark : light))
    }
}

private struct GlassShadowModifier: ViewModifier {
    let layers: [GlassShadowLayer]

    func body(content: Content) -> some View {
        let first = layers.first
        let second = layers.dropFirst().first
        content
            .shadow(color: first?.color ?? .clear, radius: first?.radius ?? 0, x: first?.x ?? 0, y: first?.y ?? 0)
            .shadow(color: second?.color ?? .clear, radius: second?.radius ?? 0, x: second?.x ?? 0, y: second?.y ?? 0)
    }
}

// MARK: - Message bubbles

/// A message bubble's outline: radius-bubble corners, with radius-tail on the
/// corner that points at the sender (bottom-leading incoming, bottom-trailing
/// outgoing) on the first bubble of a run.
enum BubbleShape {
    static func make(incoming: Bool, tail: Bool) -> UnevenRoundedRectangle {
        let r = GlassTokens.Radius.bubble
        let t = GlassTokens.Radius.tail
        return UnevenRoundedRectangle(topLeadingRadius: r,
                                      bottomLeadingRadius: incoming && tail ? t : r,
                                      bottomTrailingRadius: !incoming && tail ? t : r,
                                      topTrailingRadius: r)
    }
}

extension View {
    /// Tinted glass for a message bubble: the translucent fill (the opaque
    /// WhatsApp colour with Reduce Transparency on), the rim, and WhatsApp's
    /// hairline drop. Never a material: only the wallpaper is behind a bubble.
    func bubbleSurface(incoming: Bool, tail: Bool) -> some View {
        modifier(BubbleSurfaceModifier(incoming: incoming, tail: tail))
    }
}

private struct BubbleSurfaceModifier: ViewModifier {
    let incoming: Bool
    let tail: Bool

    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    func body(content: Content) -> some View {
        let shape = BubbleShape.make(incoming: incoming, tail: tail)
        let fill = incoming
            ? Theme.incomingBubble(scheme, reduceTransparency: reduceTransparency)
            : Theme.outgoingBubble(scheme, reduceTransparency: reduceTransparency)
        content
            // The shadow goes on the fill alone; on the whole bubble it would
            // shadow every glyph of the text too.
            .background {
                shape.fill(fill)
                    .glassShadow(light: GlassTokens.Shadow.bubbleLight,
                                 dark: GlassTokens.Shadow.bubbleDark,
                                 scheme: scheme)
            }
            .overlay {
                shape.strokeBorder(LinearGradient(colors: [GlassTokens.glassRim, GlassTokens.glassEdge],
                                                  startPoint: .top, endPoint: .center),
                                   lineWidth: 1)
                    .allowsHitTesting(false)
            }
    }
}
