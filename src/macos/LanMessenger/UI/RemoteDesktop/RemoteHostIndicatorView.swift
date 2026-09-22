import SwiftUI

// The strip a host sees while their screen is being shared.
//
// It is small, it is always on top, and it is the only thing standing between
// "I know I'm sharing" and "I forgot this was running". So it is designed for
// peripheral vision first: colour and motion carry the state, and the text is
// there for when somebody actually looks.
//
// Red for control, amber for viewing. Not a subtle tint of the accent colour —
// this is the one piece of chrome in the app that should be slightly unwelcome.

struct RemoteHostIndicatorView: View {

    let model: RemoteHostIndicatorModel
    let elapsed: String
    let onRevokeControl: () -> Void
    let onStop: () -> Void

    private var tint: Color {
        model.severity == .controlled
            ? Color(red: 0.85, green: 0.19, blue: 0.19)
            : Color(red: 0.90, green: 0.55, blue: 0.10)
    }

    var body: some View {
        HStack(spacing: 10) {
            // The one moving thing on the strip. A static dot becomes furniture
            // within a minute; a pulsing one keeps catching the eye, which is
            // the entire job.
            PulsingDot(color: .white)

            VStack(alignment: .leading, spacing: 1) {
                Text(model.headline)
                    .font(.system(size: 12, weight: .semibold))
                    .lineLimit(1)
                Text(elapsed)
                    .font(.system(size: 10, weight: .regular))
                    .monospacedDigit()
                    .opacity(0.85)
            }

            Spacer(minLength: 8)

            if model.showsRevokeControl {
                // Usually what a host actually wants: "stop touching things",
                // not "get out". Leaves the session running.
                indicatorButton("Stop Control", action: onRevokeControl)
            }
            indicatorButton("Stop Sharing", action: onStop)
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(tint, in: Capsule())
        .overlay(Capsule().strokeBorder(.white.opacity(0.22), lineWidth: 1))
        .shadow(color: .black.opacity(0.28), radius: 8, y: 2)
        .fixedSize()
    }

    private func indicatorButton(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 11, weight: .semibold))
                .padding(.horizontal, 10)
                .padding(.vertical, 4)
                .background(.white.opacity(0.22), in: Capsule())
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
