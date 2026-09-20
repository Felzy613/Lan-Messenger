import SwiftUI

// The consent prompt.
//
// This dialog is the whole security model made visible, so it is written to be
// read under the worst conditions it will actually meet: someone mid-task, who
// did not expect it, deciding in about two seconds. Four things follow from
// that, and each is a deliberate choice rather than styling:
//
//  * **The peer is named in the headline.** "Someone wants to view your screen"
//    is not a sentence anybody can act on.
//  * **The fingerprint is shown, always, in a monospaced face.** A display name
//    is trivially spoofable by anyone on the LAN; the pinned key is not, and a
//    fingerprint that is only shown when something is wrong is one nobody has
//    ever seen before and cannot compare against anything.
//  * **There is no reassuring badge on the safe path.** A green "verified" tick
//    on every prompt trains people to look for the tick and not at the words.
//    A pinned key gets a plain dialog; only the unexpected case gets colour.
//  * **Decline is the default button.** Return declines, Escape declines, and
//    the countdown declines. The prompt is a thing that happens *to* a user, so
//    every accidental path through it has to land on "no".

struct RemoteConsentView: View {

    let request: RemoteConsentRequest
    /// Seconds left before the prompt answers `timeout` on the user's behalf.
    let secondsRemaining: Int
    let onAccept: () -> Void
    let onDecline: () -> Void

    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            details
            Divider()
            footer
        }
        .frame(width: 420)
        .background(scheme == .dark ? Color(white: 0.13) : Color(white: 0.98))
    }

    // MARK: - Header

    private var header: some View {
        HStack(alignment: .top, spacing: 14) {
            AvatarView(name: request.peerName, size: 44)

            VStack(alignment: .leading, spacing: 4) {
                Text(request.title)
                    .font(.system(size: 15, weight: .semibold))
                    .fixedSize(horizontal: false, vertical: true)
                Text(request.explanation)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(16)
    }

    // MARK: - Identity

    private var details: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let warning = request.warning {
                warningRow(warning)
            }

            // About this Mac rather than about the peer, so it is deliberately
            // not the orange security treatment — an "our permissions are off"
            // note dressed as a trust warning teaches people to dismiss trust
            // warnings.
            if let notice = request.systemNotice {
                noticeRow(notice)
            }

            identityRow(label: "Address", value: request.peerIP)

            // Monospaced, and never truncated: this is the one string on the
            // dialog a careful user reads character by character against
            // something the peer read out to them.
            identityRow(label: "Key", value: request.fingerprint, monospaced: true)
        }
        .padding(16)
    }

    private func identityRow(label: String, value: String, monospaced: Bool = false) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(label)
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(.secondary)
                .frame(width: 58, alignment: .leading)
            Text(value)
                .font(.system(size: monospaced ? 12 : 12,
                              weight: monospaced ? .regular : .regular,
                              design: monospaced ? .monospaced : .default))
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
    }

    private func noticeRow(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "info.circle.fill")
                .foregroundStyle(.secondary)
                .font(.system(size: 13))
            Text(text)
                .font(.system(size: 12))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(Color.primary.opacity(scheme == .dark ? 0.08 : 0.05),
                    in: RoundedRectangle(cornerRadius: 8))
    }

    private func warningRow(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
                .font(.system(size: 13))
            Text(text)
                .font(.system(size: 12))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(Color.orange.opacity(scheme == .dark ? 0.16 : 0.12),
                    in: RoundedRectangle(cornerRadius: 8))
    }

    // MARK: - Actions

    private var footer: some View {
        HStack(spacing: 10) {
            // The countdown is stated rather than animated. A shrinking bar
            // reads as pressure to click something; a number reads as a fact.
            Text("Declines automatically in \(secondsRemaining)s")
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .monospacedDigit()

            Spacer(minLength: 0)

            Button("Decline", action: onDecline)
                .keyboardShortcut(.defaultAction)

            // Deliberately carries no keyboard shortcut. Accepting must be a
            // thing somebody aimed at and clicked.
            Button(request.acceptButtonTitle, action: onAccept)
        }
        .padding(16)
    }
}

#if DEBUG
#Preview("Pinned contact") {
    RemoteConsentView(
        request: RemoteConsentRequest(
            sessionID: "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
            kind: .viewing,
            peerName: "Dave Felzy",
            peerIP: "192.168.68.24",
            peerPublicKeyB64: Data(repeating: 7, count: 32).base64EncodedString(),
            trust: .pinned(username: "Dave Felzy"),
            expiresAt: Date().addingTimeInterval(45)),
        secondsRemaining: 45,
        onAccept: {}, onDecline: {})
}

#Preview("Changed key, asking for control") {
    RemoteConsentView(
        request: RemoteConsentRequest(
            sessionID: "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e",
            kind: .control,
            peerName: "Dave Felzy",
            peerIP: "192.168.68.24",
            peerPublicKeyB64: Data(repeating: 9, count: 32).base64EncodedString(),
            trust: .changedAtKnownAddress(username: "Dave Felzy",
                                          previousPublicKeyB64: "old"),
            expiresAt: Date().addingTimeInterval(12)),
        secondsRemaining: 12,
        onAccept: {}, onDecline: {})
}
#endif
