import SwiftUI

struct ConversationRowView: View {
    let conv: ConversationViewModel
    @EnvironmentObject var model: AppModel
    /// Whether the row is the sidebar's selection. A selected sidebar row can
    /// sit on the accent-coloured highlight, where only the hierarchical styles
    /// turn white, so it keeps them; the token colours apply everywhere else.
    /// Passed in because `backgroundProminence` needs macOS 14.
    var isSelected: Bool = false
    @State private var confirmDelete = false

    private var onSelection: Bool { isSelected }

    private func secondaryStyle(_ token: Color) -> AnyShapeStyle {
        onSelection ? AnyShapeStyle(.secondary) : AnyShapeStyle(token)
    }

    var body: some View {
        HStack(spacing: 10) {
            AvatarView(name: conv.peerName, size: GlassTokens.Size.avatarRow, photoB64: conv.photoB64)
                .overlay(alignment: .bottomTrailing) {
                    // presence-ring separates the dot from the avatar in
                    // either theme; offline is a real colour, not a faded one.
                    Circle()
                        .fill(conv.isOnline ? Theme.presenceOnline : Theme.presenceOffline)
                        .overlay(Circle().strokeBorder(Theme.presenceRing, lineWidth: 2))
                        .frame(width: GlassTokens.Size.presence, height: GlassTokens.Size.presence)
                        .offset(x: 2, y: 2)
                }
            VStack(alignment: .leading, spacing: 3) {
                HStack {
                    Text(conv.peerName)
                        .font(GlassTokens.Typography.name)
                        .lineLimit(1)
                    Spacer()
                    if let ts = conv.lastTimestamp {
                        // With unread messages the time turns semibold
                        // accent-ink, the way WhatsApp marks something new.
                        Text(Theme.formatTimestamp(ts))
                            .font(conv.unreadCount > 0 ? GlassTokens.Typography.captionStrong
                                                       : GlassTokens.Typography.caption)
                            .foregroundStyle(conv.unreadCount > 0 && !onSelection
                                             ? AnyShapeStyle(Theme.accentInk)
                                             : secondaryStyle(Theme.inkSecondary))
                    }
                }
                HStack(alignment: .top) {
                    if conv.isTyping {
                        // A miniature of the thread's typing bubble, which is
                        // how Messages marks a typing conversation in its list.
                        TypingDotsView(dotSize: 6, spacing: 4, color: Theme.accentInk)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 5)
                            .background(Theme.accentWash, in: Capsule())
                            .accessibilityElement()
                            .accessibilityLabel(Text("\(conv.peerName) is typing"))
                            .help("\(conv.peerName) is typing…")
                    } else {
                        Text(conv.lastMessage.isEmpty ? " " : conv.lastMessage)
                            .font(GlassTokens.Typography.preview)
                            .foregroundStyle(secondaryStyle(Theme.inkSecondary))
                            .lineLimit(2)
                            .multilineTextAlignment(.leading)
                    }
                    Spacer()
                    if conv.unreadCount > 0 {
                        // on-brand numerals: white on the brand green is 2:1.
                        Text("\(conv.unreadCount)")
                            .font(GlassTokens.Typography.badge)
                            .foregroundStyle(Theme.onBrand)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(Theme.accent, in: Capsule())
                    }
                    Menu {
                        if conv.isArchived {
                            Button {
                                model.unarchiveConversation(peerIP: conv.peerIP)
                            } label: { Label("Unarchive", systemImage: "tray.and.arrow.up") }
                        } else {
                            Button {
                                model.archiveConversation(peerIP: conv.peerIP)
                            } label: { Label("Archive", systemImage: "archivebox") }
                        }
                        Divider()
                        Button(role: .destructive) {
                            confirmDelete = true
                        } label: { Label("Delete conversation", systemImage: "trash") }
                    } label: {
                        // Use Color.primary.opacity() instead of .secondary (a hierarchical
                        // ShapeStyle) so the dots always render with correct contrast in dark
                        // mode — .secondary can resolve to near-invisible inside sidebar List
                        // cells on macOS 14+.
                        Image(systemName: "ellipsis")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(Color.primary.opacity(0.55))
                            .frame(width: 22, height: 22)
                            .contentShape(Rectangle())
                    }
                    .menuStyle(.borderlessButton)
                    .menuIndicator(.hidden)
                    .fixedSize()
                    .help("Conversation options")
                }
            }
        }
        .padding(.vertical, 4)
        .frame(height: GlassTokens.Size.row)
        .confirmationDialog(
            "Delete this conversation?",
            isPresented: $confirmDelete,
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) {
                model.deleteConversation(peerIP: conv.peerIP)
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("All messages with \(conv.peerName) will be removed from this device. This cannot be undone.")
        }
    }
}
