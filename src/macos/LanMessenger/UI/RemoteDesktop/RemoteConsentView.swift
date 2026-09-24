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

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            details
            footer
        }
        .frame(width: 420)
        .modifier(ConsentSurface())
    }

    // MARK: - Header

    private var header: some View {
        HStack(alignment: .top, spacing: 14) {
            AvatarView(name: request.peerName, size: 44)

            VStack(alignment: .leading, spacing: 4) {
                Text(request.title)
                    .font(GlassTokens.Typography.titleSheet)
                    .foregroundStyle(Theme.ink)
                    .fixedSize(horizontal: false, vertical: true)
                Text(request.explanation)
                    .font(GlassTokens.Typography.label)
                    .foregroundStyle(Theme.inkSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 20)
        .padding(.top, ConsentSurface.topInset)
        .padding(.bottom, 12)
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

            // The address and key card.
            VStack(alignment: .leading, spacing: 8) {
                identityRow(label: "Address", value: request.peerIP, monospaced: true)

                // Monospaced, and never truncated: this is the one string on the
                // dialog a careful user reads character by character against
                // something the peer read out to them.
                identityRow(label: "Key", value: request.fingerprint, monospaced: true)
            }
            .padding(10)
            .background(Theme.insetFill, in: RoundedRectangle(cornerRadius: GlassTokens.Radius.card))
        }
        .padding(.horizontal, 20)
    }

    private func identityRow(label: String, value: String, monospaced: Bool = false) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(label)
                .font(GlassTokens.Typography.caption)
                .foregroundStyle(Theme.inkSecondary)
                .frame(width: 58, alignment: .leading)
            Text(value)
                .font(monospaced ? GlassTokens.Typography.code : GlassTokens.Typography.label)
                .foregroundStyle(Theme.ink)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
    }

    private func noticeRow(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "info.circle.fill")
                .foregroundStyle(Theme.inkSecondary)
                .font(.system(size: 13))
            Text(text)
                .font(GlassTokens.Typography.label)
                .foregroundStyle(Theme.ink)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(Theme.insetFill, in: RoundedRectangle(cornerRadius: GlassTokens.Radius.card))
    }

    private func warningRow(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(GlassTokens.warningInk)
                .font(.system(size: 13))
            Text(text)
                .font(GlassTokens.Typography.label)
                .foregroundStyle(Theme.ink)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(GlassTokens.warningWash, in: RoundedRectangle(cornerRadius: GlassTokens.Radius.card))
    }

    // MARK: - Actions

    private var footer: some View {
        HStack(spacing: 10) {
            // The countdown is stated rather than animated. A shrinking bar
            // reads as pressure to click something; a number reads as a fact.
            Text("Declines automatically in \(secondsRemaining)s")
                .font(GlassTokens.Typography.caption)
                .foregroundStyle(Theme.inkSecondary)
                .monospacedDigit()

            Spacer(minLength: 0)

            Button("Decline", action: onDecline)
                .keyboardShortcut(.defaultAction)

            // Deliberately carries no keyboard shortcut. Accepting must be a
            // thing somebody aimed at and clicked.
            Button(request.acceptButtonTitle, action: onAccept)
        }
        .padding(EdgeInsets(top: 16, leading: 20, bottom: 20, trailing: 20))
    }
}

/// The prompt's backing. It used to be an opaque `Color(white:)` slab, which
/// is what kept the system glass out.
///
/// macOS 26: glass edge to edge, tinted with glass-thick so a security prompt
/// never turns see-through over a busy desktop; `ConsentPanel` makes the panel
/// transparent and full-size-content for it, so the content starts under the
/// title bar and `topInset` clears it. Earlier systems keep the ordinary
/// titled panel with a thick material behind the content.
struct ConsentSurface: ViewModifier {
    static var topInset: CGFloat { LiquidGlass.isAvailable ? 40 : 16 }

    func body(content: Content) -> some View {
        if LiquidGlass.isAvailable {
            content.glassSurface(.regular,
                                 in: RoundedRectangle(cornerRadius: GlassTokens.Radius.sheet),
                                 tint: GlassTokens.glassThick)
        } else {
            content.background(.thickMaterial)
        }
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
