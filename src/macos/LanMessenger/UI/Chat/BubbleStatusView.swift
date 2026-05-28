import SwiftUI

// Shared WhatsApp-style status checkmark used by all outgoing bubble types.
// Centralising this avoids the text/media/file implementations drifting apart.
struct BubbleStatusView: View {
    let status: String

    // Every pre-delivery state (Queued / Sending / Sent / unset) collapses to a
    // single grey check so the UI never flips between visually different "in flight"
    // icons. The clock was removed because it caused jarring icon transitions on
    // fast LANs and confused users who thought it indicated an error.
    var body: some View {
        switch status {
        case "Read":
            doubleCheck(color: Color(red: 0.31, green: 0.62, blue: 0.97))
        case "Delivered":
            doubleCheck(color: .secondary)
        case "Failed":
            Image(systemName: "exclamationmark.circle")
                .font(.system(size: 10))
                .foregroundStyle(.red)
        default:
            // Sent / Sending / Queued / unset all show a single grey check.
            Image(systemName: "checkmark")
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(.secondary)
        }
    }

    private func doubleCheck(color: Color) -> some View {
        ZStack(alignment: .leading) {
            Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).offset(x: 0)
            Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).offset(x: 4)
        }
        .frame(width: 14, height: 10, alignment: .leading)
        .foregroundStyle(color)
    }
}
