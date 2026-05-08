import SwiftUI

struct ConversationRowView: View {
    let conv: ConversationViewModel
    @EnvironmentObject var model: AppModel

    var body: some View {
        HStack(spacing: 10) {
            AvatarView(name: conv.peerName, size: 44)
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
                        Text("\(conv.typingSender) is typing…")
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
                }
            }
        }
        .padding(.vertical, 4)
        .contextMenu {
            Button(role: .destructive) {
                hideConversation()
            } label: {
                Label("Hide Conversation", systemImage: "eye.slash")
            }
        }
    }

    private func hideConversation() {
        if !ConfigStore.shared.config.hiddenConversations.contains(conv.peerIP) {
            ConfigStore.shared.config.hiddenConversations.append(conv.peerIP)
            ConfigStore.shared.save()
        }
        if model.selectedPeerIP == conv.peerIP {
            model.selectedPeerIP = nil
        }
    }
}
