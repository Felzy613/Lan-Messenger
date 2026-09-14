import SwiftUI

/// The three animated dots that stand in for the word "typing…" — the shape
/// every messenger has used since iMessage. The dots swell and brighten in a
/// staggered wave rather than blinking in unison, which is what makes it read
/// as someone at a keyboard instead of a progress spinner.
///
/// Used at three sizes: inside a bubble at the end of the thread, beside the
/// peer's name in the chat header, and in the sidebar row where the message
/// preview normally sits. `TypingIndicatorControl` on Windows animates on the
/// same numbers so both platforms move identically.
struct TypingDotsView: View {
    var dotSize: CGFloat = 8
    var spacing: CGFloat = 5
    /// Explicit rather than `.secondary`: the hierarchical style resolves to
    /// near-invisible inside sidebar List cells on macOS 14+ (same reason the
    /// row's options button paints with `Color.primary.opacity(...)`).
    var color: Color = Color.primary.opacity(0.45)

    /// 0.6 s out, 0.6 s back, each dot a fifth of a second behind the one
    /// before it.
    private static let pulse: Double = 0.6
    private static let stagger: Double = 0.2

    /// Reduce Motion drops the wave. Three dots still read as "typing" sitting
    /// still, so the indicator stays put rather than falling back to text.
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var pulsing = false

    var body: some View {
        HStack(spacing: spacing) {
            ForEach(0..<3, id: \.self) { index in
                Circle()
                    .fill(color)
                    // The frame stays at dotSize and the swell is a
                    // scaleEffect, so the row never reflows as it animates.
                    .frame(width: dotSize, height: dotSize)
                    .scaleEffect(reduceMotion ? 1 : (pulsing ? 1.18 : 0.82))
                    .opacity(reduceMotion ? 0.8 : (pulsing ? 1 : 0.38))
                    .animation(
                        reduceMotion
                            ? nil
                            : .easeInOut(duration: Self.pulse)
                                .repeatForever(autoreverses: true)
                                .delay(Double(index) * Self.stagger),
                        value: pulsing
                    )
            }
        }
        // Decorative: whoever hosts the dots supplies the spoken label, so
        // VoiceOver says "Alice is typing" instead of reading three circles.
        .accessibilityHidden(true)
        .onAppear { pulsing = true }
        .onDisappear { pulsing = false }
    }
}

/// The dots wrapped in an incoming bubble, shown as the last row of the thread
/// while the peer is typing — sitting exactly where their message is about to
/// land. Matches `MessageBubbleView`'s incoming geometry, tail corner included.
struct TypingBubbleView: View {
    let peerName: String
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        HStack(alignment: .bottom, spacing: 0) {
            TypingDotsView()
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
                .background(
                    Theme.incomingBubble(colorScheme),
                    in: UnevenRoundedRectangle(
                        topLeadingRadius: 16,
                        bottomLeadingRadius: 4,
                        bottomTrailingRadius: 16,
                        topTrailingRadius: 16
                    )
                )
            Spacer(minLength: 60)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 1)
        .accessibilityElement()
        .accessibilityLabel(Text("\(peerName) is typing"))
        .help("\(peerName) is typing…")
    }
}
