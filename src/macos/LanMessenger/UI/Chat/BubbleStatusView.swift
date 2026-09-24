import SwiftUI

// Shared WhatsApp-style status checkmark used by all outgoing bubble types.
// Centralising this avoids the text/media/file implementations drifting apart.
struct BubbleStatusView: View {
    let status: String
    /// Ticks only ever sit in outgoing bubbles, but they take the meta colour
    /// of whichever bubble they are in.
    var incoming: Bool = false

    private var meta: Color { Theme.meta(incoming: incoming) }

    // Every pre-delivery state (Queued / Sending / Sent / unset) collapses to a
    // single grey check so the UI never flips between visually different "in flight"
    // icons. The clock was removed because it caused jarring icon transitions on
    // fast LANs and confused users who thought it indicated an error.
    var body: some View {
        switch status {
        case "Read":
            // tick-read, which differs by theme. Read is also two ticks, so it
            // is never told apart by colour alone.
            doubleCheck(color: Theme.tickRead)
        case "Delivered":
            doubleCheck(color: meta)
        case "Failed":
            Image(systemName: "exclamationmark.circle")
                .font(.system(size: 10))
                .foregroundStyle(Theme.dangerInk)
        default:
            // Sent / Sending / Queued / unset all show a single check.
            Image(systemName: "checkmark")
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(meta)
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
