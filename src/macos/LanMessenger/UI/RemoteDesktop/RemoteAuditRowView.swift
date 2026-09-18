import SwiftUI

// An audit record in the conversation thread.
//
// Centred, small and grey: it is a fact about the conversation rather than part
// of it, and it must not read as something either party said. No bubble, no
// avatar, no timestamp alignment with either side.

struct RemoteAuditRowView: View {

    let entry: RemoteAuditEntry
    let timestamp: Date

    var body: some View {
        VStack(spacing: 2) {
            HStack(spacing: 6) {
                Image(systemName: icon)
                    .font(.system(size: 10))
                Text(entry.summary)
                    .font(.system(size: 11))
                if let duration = entry.durationSummary {
                    Text(duration)
                        .font(.system(size: 11))
                        .opacity(0.75)
                }
            }
            Text(Theme.formatTimestamp(timestamp))
                .font(.system(size: 9))
                .opacity(0.6)
        }
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .frame(maxWidth: .infinity)
        .multilineTextAlignment(.center)
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
