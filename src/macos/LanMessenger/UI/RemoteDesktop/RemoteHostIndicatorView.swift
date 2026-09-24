import SwiftUI

// The strip a host sees while their screen is being shared.
//
// It is small, it is always on top, and it is the only thing standing between
// "I know I'm sharing" and "I forgot this was running". So it is designed for
// peripheral vision first: position, motion and one red button carry it, and
// the text is there for when somebody actually looks.
//
// A neutral dark HUD, identical in light and dark mode, like the system's own
// screen-recording indicators. Colour is spent in two places only: the
// breathing dot (amber viewing, coral controlled) and the Stop Sharing button.
// A full amber or red capsule read as an alarm, and an alarm that stays up for
// a whole session teaches people to stop seeing it. Control, the riskier state,
// also gets a coral outline; the words always say which state it is.
//
// Solid, never glass: the desktop behind must not wash out a security signal.

struct RemoteHostIndicatorView: View {

    let model: RemoteHostIndicatorModel
    let elapsed: String
    let onRevokeControl: () -> Void
    let onStop: () -> Void

    private var controlled: Bool { model.severity == .controlled }

    var body: some View {
        HStack(spacing: 10) {
            // The one moving thing on the strip. A static dot becomes furniture
            // within a minute; a pulsing one keeps catching the eye, which is
            // the entire job.
            PulsingDot(color: controlled ? GlassTokens.hudSignalControl : GlassTokens.hudSignalView)

            HStack(spacing: 8) {
                Text(model.headline)
                    .font(GlassTokens.Typography.labelStrong)
                    .foregroundStyle(GlassTokens.hudInk)
                    .lineLimit(1)
                Text(elapsed)
                    .font(GlassTokens.Typography.label)
                    .monospacedDigit()
                    .foregroundStyle(GlassTokens.hudInkSecondary)
            }

            Spacer(minLength: 8)

            Rectangle()
                .fill(GlassTokens.hudRim)
                .frame(width: 1, height: 20)

            if model.showsRevokeControl {
                // Usually what a host actually wants: "stop touching things",
                // not "get out". Leaves the session running. Neutral.
                indicatorButton("Stop Control", fill: GlassTokens.hudButton, ink: GlassTokens.hudInk,
                                action: onRevokeControl)
            }
            // The only solid colour on the indicator.
            indicatorButton("Stop Sharing", fill: GlassTokens.danger, ink: GlassTokens.inkInverse,
                            action: onStop)
        }
        .padding(.leading, 16)
        .padding(.trailing, 6)
        .frame(height: 44)
        .background(GlassTokens.hudSurface, in: Capsule())
        .overlay(Capsule().strokeBorder(controlled ? GlassTokens.hudSignalControl : GlassTokens.hudRim,
                                        lineWidth: 1))
        .shadow(color: .black.opacity(0.28), radius: 8, y: 2)
        // The same HUD whatever the system theme, and the controls inside it
        // drawn for a dark surface.
        .environment(\.colorScheme, .dark)
        .fixedSize()
    }

    private func indicatorButton(_ title: String, fill: Color, ink: Color,
                                 action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(GlassTokens.Typography.labelStrong)
                .foregroundStyle(ink)
                .padding(.horizontal, 14)
                .frame(height: 32)
                .background(fill, in: Capsule())
        }
        .buttonStyle(.plain)
        .contentShape(Capsule())
    }
}

/// A dot that breathes. Deliberately slow — a fast blink reads as an error the
/// user is expected to fix, and this is a normal state they are expected to
/// notice.
private struct PulsingDot: View {
    let color: Color
    @State private var faded = false

    var body: some View {
        Circle()
            .fill(color)
            .frame(width: 8, height: 8)
            .opacity(faded ? 0.35 : 1)
            .animation(.easeInOut(duration: 1.1).repeatForever(autoreverses: true),
                       value: faded)
            .onAppear { faded = true }
    }
}

#if DEBUG
#Preview("Viewing") {
    RemoteHostIndicatorView(
        model: RemoteHostIndicatorModel(peerName: "Dave Felzy", grant: .viewing,
                                        startedAt: Date()),
        elapsed: "4:12", onRevokeControl: {}, onStop: {})
        .padding(40)
}

#Preview("Controlled") {
    RemoteHostIndicatorView(
        model: RemoteHostIndicatorModel(peerName: "Dave Felzy", grant: .control,
                                        startedAt: Date()),
        elapsed: "1:02:47", onRevokeControl: {}, onStop: {})
        .padding(40)
}
#endif
