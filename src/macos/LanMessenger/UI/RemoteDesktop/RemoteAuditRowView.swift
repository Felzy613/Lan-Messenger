import SwiftUI

// An audit record in the conversation thread.
//
// Centred, small, on a flat glass chip like WhatsApp's system messages: it is a
// fact about the conversation rather than part of it, and it must not read as
// something either party said. No bubble, no avatar, no timestamp alignment
// with either side, and no float shadow — it is part of the thread, not chrome
// over it.

struct RemoteAuditRowView: View {

    let entry: RemoteAuditEntry
    let timestamp: Date

    var body: some View {
        VStack(spacing: 2) {
            HStack(spacing: 6) {
                Image(systemName: icon)
                    .font(.system(size: 10))
                summary
                    .font(GlassTokens.Typography.caption)
                if let duration = entry.durationSummary {
                    Text(duration)
                        .font(GlassTokens.Typography.caption)
                }
            }
            // micro in ink-secondary, not dimmed any further: opacity on
            // secondary text is what took it below 4.5:1.
            Text(Theme.formatTimestamp(timestamp))
                .font(GlassTokens.Typography.micro)
        }
        .foregroundStyle(Theme.inkSecondary)
        .multilineTextAlignment(.center)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .glassSurface(.regular, in: Capsule())
        .padding(.horizontal, 12)
        .padding(.vertical, 4)
        .frame(maxWidth: .infinity)
    }

    /// The sentence with its actor — the peer, or "You" — in semibold ink. The
    /// wording itself (host or viewer form, from `viewing`) is untouched.
    private var summary: Text {
        let sentence = entry.summary
        for actor in [entry.peerName, "You"] where !actor.isEmpty && sentence.hasPrefix(actor) {
            return Text(actor).fontWeight(.semibold).foregroundColor(Theme.ink)
                + Text(sentence.dropFirst(actor.count))
        }
        return Text(sentence)
    }

    /// Control gets the stronger symbol. The trail is skimmed, and the two
    /// events it most matters to tell apart are "they watched" and "they drove".
    private var icon: String {
        switch entry.event {
        case .sessionStarted: return "eye"
        case .controlGranted: return "hand.raised.fill"
        case .controlRevoked: return "hand.raised.slash"
        case .sessionEnded:   return "stop.circle"
        }
    }
}
