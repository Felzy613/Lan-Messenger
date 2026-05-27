import SwiftUI

struct ConversationRowView: View {
    let conv: ConversationViewModel
    @EnvironmentObject var model: AppModel
    @State private var confirmDelete = false

    var body: some View {
        HStack(spacing: 10) {
            AvatarView(name: conv.peerName, size: 44, photoB64: conv.photoB64)
                .overlay(alignment: .bottomTrailing) {
                    Circle()
                        .fill(conv.isOnline ? Color.green : Color.gray.opacity(0.45))
                        .frame(width: 11, height: 11)
                        .offset(x: 2, y: 2)
                }
            VStack(alignment: .leading, spacing: 3) {
                HStack {
                    Text(conv.peerName)
                        .font(.system(size: 14, weight: .semibold))
                    Spacer()
                    if let ts = conv.lastTimestamp {
                        Text(Theme.formatTimestamp(ts))
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                    }
                }
                HStack {
                    if conv.isTyping {
                        Text("typing…")
                            .font(.system(size: 13))
                            .foregroundStyle(Theme.accent)
                            .italic()
                    } else {
                        Text(conv.lastMessage.isEmpty ? " " : conv.lastMessage)
                            .font(.system(size: 13))
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    Spacer()
                    if conv.unreadCount > 0 {
                        Text("\(conv.unreadCount)")
                            .font(.system(size: 11, weight: .bold))
                            .foregroundStyle(.white)
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
