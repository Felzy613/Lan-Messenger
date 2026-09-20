import SwiftUI
import UniformTypeIdentifiers

struct ChatView: View {
    @EnvironmentObject var model: AppModel
    let peerIP: String
    @Environment(\.colorScheme) var colorScheme
    @Environment(\.controlActiveState) var controlActiveState

    @State private var replyTarget: MessageEntry? = nil
    /// Non-nil while the composer is editing an already-sent message instead of
    /// writing a new one.
    @State private var editTarget: MessageEntry? = nil
    @State private var scrollHighlightID: String? = nil
    /// True while a file drag is hovering anywhere over the thread.
    @State private var isDropTargeted = false
    /// Scroll geometry for the thread, measured rather than read: SwiftUI
    /// exposes no scroll offset for a ScrollView before macOS 15, so the
    /// distance still to scroll is `contentBottom - viewportHeight`.
    @State private var contentBottom: CGFloat = 0
    @State private var viewportHeight: CGFloat = 0
    /// Height of the message content itself, independent of where the thread
    /// is scrolled. `contentBottom` conflates the two, so this is what tells
    /// a bubble growing apart from the reader scrolling away from it.
    @State private var contentHeight: CGFloat = 0
    /// Whether the thread is following new content down. Latched rather than
    /// recomputed on the spot: geometry taken mid-scroll reads as "adrift",
    /// and acting on that reading is how a thread stops following at all.
    @State private var pinnedToBottom = true
    /// True from the moment a scroll-to-bottom is asked for until the repeats
    /// below have run. Geometry arriving in that window is in flight and must
    /// not unpin us, and the jump button must not flash.
    @State private var isSettling = false
    @State private var settleUntil: Date = .distantPast

    /// Slack enough that resting at the bottom still counts as "at the bottom"
    /// after a bubble's height settles. Matches the Windows threshold.
    private static let atBottomSlack: CGFloat = 40
    /// A scroll-to-bottom is repeated at these offsets (seconds) rather than
    /// performed once. SwiftUI has not laid the new row out when the change
    /// fires, so a single `scrollTo` lands on the *old* bottom and leaves the
    /// message that caused it just off screen.
    private static let settleDelays: [TimeInterval] = [0.0, 0.05, 0.15, 0.35, 0.6]
    private static let scrollSpace = "chatScroll"
    /// Identity of the row that always sits at the very end of the thread —
    /// the typing bubble when the peer is typing, a zero-height placeholder
    /// when they are not. Scrolling to it lands on the real bottom either way.
    private static let threadEndID = "__thread_end__"

    private var distanceFromBottom: CGFloat { max(0, contentBottom - viewportHeight) }
    private var isNearBottom: Bool { distanceFromBottom < Self.atBottomSlack }
    private var showJumpButton: Bool { !isNearBottom && !isSettling }

    private var conv: ConversationViewModel? {
        model.conversations.first { $0.peerIP == peerIP }
    }

    private var entries: [MessageEntry] {
        model.messages[peerIP] ?? []
    }

    private var peerIsOnline: Bool {
        model.peers.values.first { $0.ip == peerIP }?.isOnline ?? false
    }

    private var peerIsTyping: Bool {
        model.typingStates[peerIP]?.active ?? false
    }

    private var peerName: String {
        conv?.peerName ?? peerIP
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
            if let editing = editTarget {
                editBanner(for: editing)
                    .transition(.opacity)
            } else if let reply = replyTarget {
                replyBanner(for: reply)
                    .transition(.opacity)
            }
            Divider()
            ComposerView(peerIP: peerIP, replyTarget: $replyTarget, editTarget: $editTarget)
                .environmentObject(model)
                .background(.bar)
        }
        .background(Theme.chatBackground(colorScheme))
        // The whole thread is the drop target, not just the composer strip.
        // Aiming at a 48 pt-tall bar at the bottom of the window is the kind of
        // thing you only get right on the second try; dropping anywhere on the
        // conversation you are looking at is what every other messenger does.
        .onDrop(of: AttachmentPasteboard.dropTypes, isTargeted: $isDropTargeted) { providers in
            handleDrop(providers)
        }
        .overlay { if isDropTargeted { dropOverlay } }
        // controlActiveState is .key/.active when the window is on screen,
        // .inactive when minimized or the app is backgrounded. Only send read
        // receipts when the user can actually see the thread.
        .onAppear { if controlActiveState != .inactive { markRead() } }
        .onChange(of: entries.count) { _ in if controlActiveState != .inactive { markRead() } }
        .onChange(of: controlActiveState) { state in if state != .inactive { markRead() } }
    }

    // MARK: - Drag and drop

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        let fileURLType = UTType.fileURL.identifier
        guard providers.contains(where: { $0.hasItemConformingToTypeIdentifier(fileURLType) }) else {
            return false
        }
        AttachmentPasteboard.loadDroppedPaths(from: providers) { paths in
            NetLogger.ui(event: "attachment_dropped", peer: peerIP, detail: "\(paths.count) file(s)")
            for path in paths {
                model.sendFile(path: path, toPeerIP: peerIP)
            }
        }
        return true
    }

    private var dropOverlay: some View {
        ZStack {
            Theme.accent.opacity(0.08)
            RoundedRectangle(cornerRadius: 16)
                .strokeBorder(Theme.accent,
                              style: StrokeStyle(lineWidth: 2, dash: [8, 6]))
                .padding(10)
            VStack(spacing: 8) {
                Image(systemName: "paperclip.circle.fill")
                    .font(.system(size: 38))
                    .foregroundStyle(Theme.accent)
                Text("Drop to send to \(conv?.peerName ?? peerIP)")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.secondary)
            }
        }
        .allowsHitTesting(false)
        .transition(.opacity)
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
                // The dots replace the caption rather than sitting beside it,
                // so the header keeps its height and the peer's state is still
                // legible while the thread is scrolled up and the in-thread
                // typing bubble is off screen.
                if peerIsTyping {
                    TypingDotsView(dotSize: 5, spacing: 3, color: Color.primary.opacity(0.5))
                        .frame(height: 13, alignment: .leading)
                        // The dots themselves are accessibility-hidden; this
                        // wrapper is what VoiceOver actually reads.
                        .accessibilityElement()
                        .accessibilityLabel(Text("\(peerName) is typing"))
                        .help("\(peerName) is typing…")
                        .transition(.opacity)
                } else {
                    Text(peerIsOnline ? "Online" : "Offline")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .transition(.opacity)
                }
            }
            .animation(.easeInOut(duration: 0.2), value: peerIsTyping)
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
                // Zero spacing so the sentinels below sit exactly on the content
                // edges; the implicit container's default spacing would otherwise
                // land the scroll a stray few points short of the bottom.
                VStack(spacing: 0) {
                    // Top edge of the content, in the scroll view's own coordinate
                    // space. Paired with the bottom sentinel it gives the content
                    // height, and both arrive in one preference so the two can
                    // never be read from different layout passes — see
                    // `onPreferenceChange` below.
                    GeometryReader { geo in
                        Color.clear.preference(
                            key: ScrollGeometryKey.self,
                            value: ScrollGeometry(top: geo.frame(in: .named(Self.scrollSpace)).minY)
                        )
                    }
                    .frame(height: 0)

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
                                onReply: { withAnimation { editTarget = nil; replyTarget = entry } },
                                onTapReplyTarget: {
                                    guard let targetId = entry.replyToMessageId,
                                          let match = entries.first(where: { $0.messageId == targetId }) else { return }
                                    withAnimation { proxy.scrollTo(match.id, anchor: .center) }
                                    scrollHighlightID = match.id
                                },
                                replyFilePath: resolvedReplyFilePath(for: entry),
                                onDelete: { forEveryone in
                                    model.deleteMessage(entry, peerIP: peerIP, forEveryone: forEveryone)
                                },
                                onEdit: { withAnimation { replyTarget = nil; editTarget = entry } }
                            )
                            .id(entry.id)
                            .background(
                                scrollHighlightID == entry.id
                                ? Theme.accent.opacity(0.10)
                                : Color.clear
                            )
                        }

                        // The typing bubble is the last row of the thread, where
                        // the peer's message is about to appear. The wrapper is
                        // always in the layout — zero-height when nobody is typing
                        // — so `threadEndID` is a stable scroll target and the
                        // insertion animates on its own without putting an
                        // .animation() over the whole message list.
                        VStack(spacing: 0) {
                            if peerIsTyping {
                                TypingBubbleView(peerName: peerName)
                                    .transition(.opacity)
                            }
                        }
                        .id(Self.threadEndID)
                        .animation(.easeInOut(duration: 0.18), value: peerIsTyping)
                    }
                    .padding(.vertical, 12)

                    // Bottom edge of the content: its maxY in the scroll view's own
                    // coordinate space is where the bottom of the thread currently
                    // sits. Subtracting the viewport height gives the distance still
                    // to scroll, which is what drives both the jump button and the
                    // "don't yank the reader" check below.
                    //
                    // Both sentinels have to be real siblings of the content, not
                    // .background() on it: preferences raised inside a background
                    // subtree never reach .onPreferenceChange here (verified on
                    // macOS 13/14 — the value stays at the default forever).
                    GeometryReader { geo in
                        Color.clear.preference(
                            key: ScrollGeometryKey.self,
                            value: ScrollGeometry(bottom: geo.frame(in: .named(Self.scrollSpace)).maxY)
                        )
                    }
                    .frame(height: 0)
                }
            }
            .coordinateSpace(name: Self.scrollSpace)
            // Viewport height, read straight out of the geometry rather than
            // through a preference, for the same reason.
            .background(
                GeometryReader { geo in
                    Color.clear
                        .onAppear { viewportHeight = geo.size.height }
                        .onChange(of: geo.size.height) { viewportHeight = $0 }
                }
            )
            .onPreferenceChange(ScrollGeometryKey.self) { geometry in
                guard let top = geometry.top, let bottom = geometry.bottom else { return }
                let newHeight = bottom - top
                let grew = newHeight > contentHeight + 0.5
                contentHeight = newHeight
                contentBottom = bottom

                if grew, pinnedToBottom {
                    // A bubble settled — a thumbnail finished decoding, a line
                    // rewrapped — and pushed the newest message below the fold.
                    // Follow it down, off this layout pass: scrolling from
                    // inside one re-enters layout and SwiftUI complains about
                    // state written during a view update.
                    //
                    // Growth must be handled here rather than unpinning: the
                    // reading that arrives with it says we are adrift by
                    // exactly the amount the content just grew, and acting on
                    // that is how the thread stops following at all.
                    DispatchQueue.main.async { pinToBottom(proxy: proxy, animated: false) }
                } else if !isSettling, !grew {
                    // Only a reading taken at rest, with the content the size
                    // it already was, gets to decide the reader has scrolled
                    // away.
                    pinnedToBottom = max(0, bottom - viewportHeight) < Self.atBottomSlack
                }
            }
            .overlay(alignment: .bottomTrailing) {
                jumpToLatestButton { pinToBottom(proxy: proxy, animated: true) }
            }
            .onAppear { pinToBottom(proxy: proxy, animated: false) }
            .onChange(of: entries.count) { _ in
                // Sending always jumps to the newest message. Receiving only
                // does when the newest message is already on screen — otherwise
                // an arriving message would snatch the thread away from someone
                // reading back through history. The jump button is how they get
                // back down.
                let outgoing = entries.last.map { !$0.incoming } ?? false
                if outgoing || pinnedToBottom {
                    pinToBottom(proxy: proxy, animated: true)
                }
            }
            .onChange(of: peerIsTyping) { nowTyping in
                // Same rule as an arriving message: follow the thread down only
                // when the reader is already at the bottom.
                guard nowTyping, pinnedToBottom else { return }
                pinToBottom(proxy: proxy, animated: true)
            }
            .onChange(of: controlActiveState) { state in
                // Re-showing the window — from the tray, the Dock, or a
                // deminiaturize — re-activates the scene without re-running
                // onAppear, and the thread is left wherever the geometry was
                // when it went away. Land on the newest message again.
                guard state != .inactive, pinnedToBottom else { return }
                pinToBottom(proxy: proxy, animated: false)
            }
            .onChange(of: scrollHighlightID) { newValue in
                guard newValue != nil else { return }
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
                    withAnimation { scrollHighlightID = nil }
                }
            }
        }
    }

    /// Floating jump-to-latest control, shown only while the newest message is
    /// off screen.
    private func jumpToLatestButton(action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: "chevron.down")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(Theme.accent)
                .frame(width: 30, height: 30)
                .background(
                    Circle()
                        .fill(.regularMaterial)
                        .overlay(Circle().strokeBorder(Color.primary.opacity(0.08)))
                        .shadow(color: .black.opacity(0.18), radius: 4, y: 1)
                )
        }
        .buttonStyle(.plain)
        .help("Jump to latest")
        .padding(.trailing, 18)
        .padding(.bottom, 10)
        // Hidden while a scroll is settling too: the geometry is mid-flight
        // there and would flash the button on for a frame or two.
        .opacity(showJumpButton ? 1 : 0)
        // Kept in the layout but inert when hidden, so a fade-out never eats a
        // click aimed at the bubble underneath it.
        .allowsHitTesting(showJumpButton)
        .animation(.easeInOut(duration: 0.15), value: showJumpButton)
    }

    /// Scrolls to the newest message and keeps doing so for a moment
    /// afterwards, while the rows that triggered it finish laying out.
    private func pinToBottom(proxy: ScrollViewProxy, animated: Bool) {
        pinnedToBottom = true
        isSettling = true
        let deadline = Date().addingTimeInterval(Self.settleDelays.last ?? 0)
        settleUntil = deadline

        scrollToBottom(proxy: proxy, animated: animated)
        for delay in Self.settleDelays.dropFirst() {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                // A later pin supersedes this schedule; its own repeats cover
                // the rest of the window.
                guard settleUntil <= deadline else { return }
                scrollToBottom(proxy: proxy, animated: false)
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + (Self.settleDelays.last ?? 0) + 0.05) {
            guard settleUntil <= deadline else { return }
            isSettling = false
        }
    }

    private func scrollToBottom(proxy: ScrollViewProxy, animated: Bool) {
        // Target the thread-end anchor, not the last message: when the peer is
        // typing the bubble sits below the newest message, and scrolling to
        // the message would leave the thing we are scrolling for off screen.
        guard !entries.isEmpty || peerIsTyping else { return }
        if animated {
            withAnimation(.easeOut(duration: 0.15)) { proxy.scrollTo(Self.threadEndID, anchor: .bottom) }
        } else {
            proxy.scrollTo(Self.threadEndID, anchor: .bottom)
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

    // MARK: - Edit banner above composer

    private func editBanner(for entry: MessageEntry) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Rectangle().fill(Theme.accent).frame(width: 3, height: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text("Editing message")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(Theme.accent)
                Text(MessagingService.replyPreviewText(for: entry))
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            Button {
                withAnimation { editTarget = nil }
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 16))
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("Cancel editing (Esc)")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 6)
        .background(.bar)
    }

    // MARK: - Reply file path lookup

    // Returns the local file path for the message that `entry` is replying to,
    // or nil if the original message is not a file or cannot be found in history.
    // Used by ReplyChipView to show a thumbnail instead of plain text.
    private func resolvedReplyFilePath(for entry: MessageEntry) -> String? {
        guard let id = entry.replyToMessageId else { return nil }
        guard let orig = entries.first(where: { $0.messageId == id }) else { return nil }
        let text = orig.text
        guard text.hasPrefix("__FILE__:") else { return nil }
        return String(text.dropFirst("__FILE__:".count))
    }

    // MARK: - Read receipts

    private func markRead() {
        model.markConversationRead(peerIP: peerIP)
    }
}

// MARK: - Scroll geometry

/// Both edges of the thread's content, measured in the scroll view's own
/// coordinate space. `bottom` is roughly the viewport height when the newest
/// message is fully on screen and larger by the remaining scroll distance
/// otherwise; `bottom - top` is the content height.
///
/// They travel as one value on purpose. Read through separate callbacks, the
/// distance-to-bottom can arrive before the height it belongs to, and a thread
/// that grew looks exactly like a thread the reader scrolled away from.
private struct ScrollGeometry: Equatable {
    var top: CGFloat?
    var bottom: CGFloat?
}

private struct ScrollGeometryKey: PreferenceKey {
    static var defaultValue = ScrollGeometry()
    static func reduce(value: inout ScrollGeometry, nextValue: () -> ScrollGeometry) {
        let next = nextValue()
        if let top = next.top { value.top = top }
        if let bottom = next.bottom { value.bottom = bottom }
    }
}
