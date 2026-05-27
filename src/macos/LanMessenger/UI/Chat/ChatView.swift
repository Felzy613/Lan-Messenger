import SwiftUI

struct ChatView: View {
    @EnvironmentObject var model: AppModel
    let peerIP: String
    @Environment(\.colorScheme) var colorScheme

    @State private var replyTarget: MessageEntry? = nil
    @State private var scrollHighlightID: String? = nil

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
            if let reply = replyTarget {
                replyBanner(for: reply)
                    .transition(.opacity)
            }
            Divider()
            ComposerView(peerIP: peerIP, replyTarget: $replyTarget)
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
            AvatarView(name: conv?.peerName ?? "?", size: 36, photoB64: conv?.photoB64)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(conv?.peerName ?? peerIP)
                        .font(.system(size: 14, weight: .semibold))
                    Circle()
                        .fill(peerIsOnline ? Color.green : Color.gray)
                        .frame(width: 8, height: 8)
                }
                if let typing = model.typingStates[peerIP], typing.active {
                    Text("typing…")
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
                // Use VStack (not LazyVStack) because history is capped at 200 messages.
                // LazyVStack + proxy.scrollTo() forces SwiftUI to materialise and measure
                // every cell to compute the scroll destination, defeating lazy loading and
                // causing the main-thread hang observed in the hang reports.  A plain VStack
                // renders all rows once up-front, which is cheap for ≤200 messages and
                // eliminates the DynamicContainerInfo layout-cycle that LazyVStack triggers
                // when many MediaBubbleView tasks complete concurrently.
                VStack(spacing: 2) {
                    ForEach(Array(entries.enumerated()), id: \.element.id) { idx, entry in
                        let prevIncoming = idx > 0 ? entries[idx - 1].incoming : !entry.incoming
                        MessageBubbleView(
                            entry: entry,
                            isFirstInRun: entry.incoming != prevIncoming,
                            onReply: { withAnimation { replyTarget = entry } },
                            onTapReplyTarget: {
                                guard let targetId = entry.replyToMessageId,
                                      let match = entries.first(where: { $0.messageId == targetId }) else { return }
                                withAnimation { proxy.scrollTo(match.id, anchor: .center) }
                                scrollHighlightID = match.id
                            }
                        )
                        .id(entry.id)
                        .background(
                            scrollHighlightID == entry.id
                            ? Theme.accent.opacity(0.10)
                            : Color.clear
                        )
                    }
                }
                .padding(.vertical, 12)
            }
            .onAppear { scrollToBottom(proxy: proxy, animated: false) }
            .onChange(of: entries.count) { _ in scrollToBottom(proxy: proxy, animated: true) }
            .onChange(of: scrollHighlightID) { newValue in
                guard newValue != nil else { return }
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
                    withAnimation { scrollHighlightID = nil }
                }
            }
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

    // MARK: - Reply banner above composer

    private func replyBanner(for reply: MessageEntry) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Rectangle().fill(Theme.accent).frame(width: 3, height: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text("Replying to \(reply.incoming ? reply.sender : "yourself")")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(Theme.accent)
                Text(MessagingService.replyPreviewText(for: reply))
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            Button {
                withAnimation { replyTarget = nil }
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 16))
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 6)
        .background(.bar)
    }

    // MARK: - Read receipts

    private func markRead() {
        model.markConversationRead(peerIP: peerIP)
    }
}
