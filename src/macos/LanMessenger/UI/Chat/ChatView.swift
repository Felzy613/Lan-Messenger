import SwiftUI

struct ChatView: View {
    @EnvironmentObject var model: AppModel
    let peerIP: String
    @Environment(\.colorScheme) var colorScheme

    private var conv: ConversationViewModel? {
        model.conversations.first { $0.peerIP == peerIP }
    }

    private var entries: [MessageEntry] {
        model.messages[peerIP] ?? []
    }

    private var peerIsOnline: Bool {
        model.peers.values.first { $0.ip == peerIP }?.isOnline ?? false
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            messageList
            if let transfer = model.activeTransfers[peerIP] {
                Divider()
                FileTransferBannerView(
                    label: transfer.label,
                    bytes: transfer.bytes,
                    total: transfer.total
                )
            }
            Divider()
            ComposerView(peerIP: peerIP)
                .environmentObject(model)
                .background(.bar)
        }
        .background(Theme.chatBackground(colorScheme))
        .onAppear { markRead() }
        .onChange(of: entries.count) { _ in markRead() }
    }

    // MARK: - Header

    private var header: some View {
        HStack(spacing: 10) {
            AvatarView(name: conv?.peerName ?? "?", size: 36)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(conv?.peerName ?? peerIP)
                        .font(.system(size: 14, weight: .semibold))
                    Circle()
                        .fill(peerIsOnline ? Color.green : Color.gray)
                        .frame(width: 8, height: 8)
                }
                if let typing = model.typingStates[peerIP], typing.active {
                    Text("\(typing.sender) is typing…")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .transition(.opacity)
                        .animation(.easeInOut(duration: 0.2), value: typing.active)
                } else {
                    Text(peerIsOnline ? "Online" : "Offline")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }
            Spacer()
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .background(.bar)
    }

    // MARK: - Message list

    private var messageList: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 2) {
                    ForEach(Array(entries.enumerated()), id: \.element.id) { idx, entry in
                        let prevIncoming = idx > 0 ? entries[idx - 1].incoming : !entry.incoming
                        MessageBubbleView(
                            entry: entry,
                            isFirstInRun: entry.incoming != prevIncoming
                        )
                        .id(entry.id)
                    }
                }
                .padding(.vertical, 12)
            }
            .onAppear { scrollToBottom(proxy: proxy, animated: false) }
            .onChange(of: entries.count) { _ in scrollToBottom(proxy: proxy, animated: true) }
        }
    }

    private func scrollToBottom(proxy: ScrollViewProxy, animated: Bool) {
        guard let lastID = entries.last?.id else { return }
        if animated {
            withAnimation(.easeOut(duration: 0.15)) { proxy.scrollTo(lastID, anchor: .bottom) }
        } else {
            proxy.scrollTo(lastID, anchor: .bottom)
        }
    }

    // MARK: - Read receipts

    private func markRead() {
        for entry in entries {
            model.sendReadReceipt(for: entry, peerIP: peerIP)
        }
    }
}
