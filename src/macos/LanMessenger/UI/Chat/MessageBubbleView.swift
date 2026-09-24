import SwiftUI
import AppKit

struct MessageBubbleView: View {
    let entry: MessageEntry
    let isFirstInRun: Bool
    var onReply: (() -> Void)? = nil
    var onTapReplyTarget: (() -> Void)? = nil
    /// Local file path of the replied-to message (if it was a media/file message),
    /// resolved by ChatView from conversation history. Nil for text replies or when
    /// the original message is not found.
    var replyFilePath: String? = nil
    /// Called when the user chooses a delete option from the context menu.
    /// The Bool is `forEveryone` — true for "Delete for Everyone", false for "Delete for Me".
    var onDelete: ((Bool) -> Void)? = nil
    /// Called when the user picks "Edit" — the composer takes over from there.
    var onEdit: (() -> Void)? = nil
    /// False in a legacy thread, which has no identity key behind it:
    /// "Delete for Everyone" would have nobody to tell.
    var canReachPeer: Bool = true
    @Environment(\.colorScheme) var colorScheme
    // Tracks whether the received file still exists on disk (checked asynchronously).
    @State private var fileExists = false
    // Surfaces FinderReveal errors (missing file, permissions) as an alert.
    @State private var revealError: String? = nil

    // File messages use a "__FILE__:/path/to/file" prefix stored by AppModel.
    private var filePath: String? {
        entry.text.hasPrefix("__FILE__:")
            ? String(entry.text.dropFirst("__FILE__:".count))
            : nil
    }

    /// The media classification for the attached file, or `.other` if this is a text bubble.
    /// Images and videos render through `MediaBubbleView` for an inline preview.
    private var mediaKind: MediaKind {
        guard let path = filePath else { return .other }
        return MediaKind.from(path: path)
    }

    var body: some View {
        HStack(alignment: .bottom, spacing: 0) {
            if entry.incoming {
                bubble
                Spacer(minLength: 60)
            } else {
                Spacer(minLength: 60)
                bubble
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 1)
    }

    @ViewBuilder
    private var bubble: some View {
        if entry.deleted {
            deletedBubble
        } else if let path = filePath {
            // Photos and videos get an inline media bubble; everything else
            // falls through to the generic "document" bubble.  MediaBubbleView
            // handles its own file-existence check so it can render a
            // "missing file" placeholder for moved/deleted media.
            switch mediaKind {
            case .image, .video:
                MediaBubbleView(
                    entry: entry,
                    isFirstInRun: isFirstInRun,
                    kind: mediaKind,
                    onReply: onReply,
                    onTapReplyTarget: onTapReplyTarget,
                    replyFilePath: replyFilePath,
                    onDelete: onDelete,
                    canReachPeer: canReachPeer
                )
            case .other:
                fileBubble(path: path)
            }
        } else if entry.incoming {
            incomingBubble
        } else {
            outgoingBubble
        }
    }

    // MARK: - Deleted placeholder bubble

    // Rendered in place of the normal text/file/image content when
    // `entry.deleted == true`. Reply-chip and file-action UI are suppressed.
    private var deletedBubble: some View {
        HStack(spacing: 6) {
            Image(systemName: "trash")
                .font(.system(size: 11))
            Text("This message was deleted")
                .font(GlassTokens.Typography.preview)
                .italic()
        }
        .foregroundStyle(meta)
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .bubbleSurface(incoming: entry.incoming, tail: isFirstInRun)
        .frame(maxWidth: GlassTokens.Size.bubbleMax, alignment: entry.incoming ? .leading : .trailing)
    }

    /// Time, "edited", ticks and the relay badge: ink-secondary on an incoming
    /// bubble, meta-out on an outgoing one (ink-secondary fails on the green).
    private var meta: Color { Theme.meta(incoming: entry.incoming) }

    // MARK: - Reply preview chip (shown at top of bubble when replying)

    @ViewBuilder
    private var replyChip: some View {
        if let preview = entry.replyToPreview, !preview.isEmpty {
            ReplyChipView(
                preview: preview,
                sender: entry.replyToSender,
                filePath: replyFilePath,
                incoming: entry.incoming,
                onTap: onTapReplyTarget
            )
        }
    }

    // MARK: - File bubble

    private func fileBubble(path: String) -> some View {
        let url  = URL(fileURLWithPath: path)
        let name = url.lastPathComponent

        return VStack(alignment: .leading, spacing: 6) {
            replyChip
            HStack(spacing: 10) {
                Image(systemName: "doc.fill")
                    .font(.system(size: 28))
                    .foregroundStyle(Theme.accentInk(incoming: entry.incoming))
                VStack(alignment: .leading, spacing: 3) {
                    Text(name)
                        .font(GlassTokens.Typography.fileName)
                        .foregroundStyle(Theme.ink)
                        .lineLimit(2)
                    HStack(spacing: 4) {
                        Text(formattedTime)
                            .font(GlassTokens.Typography.meta)
                            .foregroundStyle(meta)
                        relayBadge
                        if !entry.incoming { statusIcon }
                    }
                }
                Spacer(minLength: 0)
                if fileExists {
                    // "Open" launches the file with the default macOS app for its
                    // type (Preview, Pages, etc.); "Show" reveals it in Finder.
                    // Both run via NSWorkspace / FinderReveal which dispatch off
                    // the main thread so the chat list never stutters.
                    Button {
                        NSWorkspace.shared.open(url)
                    } label: {
                        Text("Open")
                            .font(GlassTokens.Typography.captionStrong)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 4)
                            .background(Theme.accentWash, in: Capsule())
                            .foregroundStyle(Theme.accentInk(incoming: entry.incoming))
                    }
                    .buttonStyle(.plain)
                    .help("Open \(name) with the default app")

                    Button {
                        FinderReveal.reveal(path: path) { msg in revealError = msg }
                    } label: {
                        Text("Show")
                            .font(GlassTokens.Typography.captionStrong)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 4)
                            .background(Theme.insetFill, in: Capsule())
                            .foregroundStyle(meta)
                    }
                    .buttonStyle(.plain)
                    .help("Show \(name) in Finder")
                } else {
                    Text("Deleted")
                        .font(GlassTokens.Typography.caption)
                        .foregroundStyle(meta)
                }
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .bubbleSurface(incoming: entry.incoming, tail: isFirstInRun)
        .frame(maxWidth: GlassTokens.Size.fileBubbleMax)
        .task(id: path) {
            // Check file existence off the main thread so the view body stays non-blocking.
            let result = await Task.detached(priority: .utility) {
                FileManager.default.fileExists(atPath: path)
            }.value
            fileExists = result
        }
        .contextMenu {
            if onReply != nil {
                Button { onReply?() } label: { Label("Reply", systemImage: "arrowshape.turn.up.left") }
                Divider()
            }
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(path, forType: .string)
            } label: { Label("Copy Path", systemImage: "doc.on.doc") }
            if fileExists {
                Button {
                    FinderReveal.reveal(path: path) { msg in revealError = msg }
                } label: { Label("Show in Finder", systemImage: "folder") }
                Button {
                    // Open with the default app. NSWorkspace.open is async and
                    // does not block the UI thread.
                    NSWorkspace.shared.open(url)
                } label: { Label("Open", systemImage: "square.and.arrow.up") }
            }
            deleteMenuItems(allowDeleteForEveryone: !entry.incoming)
        }
        // Same drag-out affordance as the media bubbles, and the same ordering
        // requirement: .onDrag must wrap .contextMenu, not the reverse, or the
        // context-menu wrapper owns the mouse-down and no drag ever starts.
        // Gated on fileExists so a bubble whose file was moved or deleted
        // doesn't start a drag that delivers nothing.
        .onDrag {
            guard fileExists, let provider = AttachmentPasteboard.outgoingProvider(forFileAt: path) else {
                return NSItemProvider()
            }
            return provider
        }
        .alert("Cannot open file location",
               isPresented: Binding(get: { revealError != nil },
                                    set: { if !$0 { revealError = nil } })) {
            Button("OK", role: .cancel) { }
        } message: {
            Text(revealError ?? "")
        }
    }

    // MARK: - Incoming text bubble

    // No sender name above the bubble. Every conversation here is one-to-one,
    // the header already names the peer, and every incoming bubble in the
    // thread is from that one person — so the label repeated their name down
    // the whole thread and told the reader nothing. `isFirstInRun` still
    // decides the tail corner, which is what actually groups a run visually.
    private var incomingBubble: some View {
        VStack(alignment: .leading, spacing: 2) {
            VStack(alignment: .leading, spacing: 4) {
                replyChip
                Text(entry.text)
                    .font(GlassTokens.Typography.message)
                    .foregroundStyle(Theme.ink)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
                HStack(spacing: 4) {
                    editedMarker
                    Text(formattedTime)
                        .font(GlassTokens.Typography.meta)
                        .foregroundStyle(meta)
                    relayBadge
                }
                .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .bubbleSurface(incoming: true, tail: isFirstInRun)
        }
        .frame(maxWidth: GlassTokens.Size.bubbleMax, alignment: .leading)
        .contextMenu {
            if onReply != nil {
                Button { onReply?() } label: { Label("Reply", systemImage: "arrowshape.turn.up.left") }
                Divider()
            }
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(entry.text, forType: .string)
            } label: { Label("Copy", systemImage: "doc.on.doc") }
            deleteMenuItems(allowDeleteForEveryone: false)
        }
    }

    // MARK: - Outgoing text bubble

    private var outgoingBubble: some View {
        VStack(alignment: .trailing, spacing: 4) {
            replyChip
            Text(entry.text)
                .font(GlassTokens.Typography.message)
                .foregroundStyle(Theme.ink)
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 4) {
                Spacer(minLength: 0)
                editedMarker
                Text(formattedTime)
                    .font(GlassTokens.Typography.meta)
                    .foregroundStyle(meta)
                relayBadge
                statusIcon
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .bubbleSurface(incoming: false, tail: isFirstInRun)
        .frame(maxWidth: GlassTokens.Size.bubbleMax, alignment: .trailing)
        .contextMenu {
            if onReply != nil {
                Button { onReply?() } label: { Label("Reply", systemImage: "arrowshape.turn.up.left") }
                Divider()
            }
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(entry.text, forType: .string)
            } label: { Label("Copy", systemImage: "doc.on.doc") }
            if canEdit {
                Button { onEdit?() } label: { Label("Edit", systemImage: "pencil") }
            }
            deleteMenuItems(allowDeleteForEveryone: true)
        }
    }

    // MARK: - Helpers

    /// Only our own outgoing text messages can be edited. An attachment's text
    /// is a local file path rather than a body, and a deleted message has no
    /// body left to replace.
    private var canEdit: Bool {
        onEdit != nil
            && !entry.incoming
            && !entry.deleted
            && entry.messageId != nil
            && filePath == nil
    }

    @ViewBuilder
    private var editedMarker: some View {
        if entry.edited && !entry.deleted {
            Text("edited")
                .font(GlassTokens.Typography.meta)
                .foregroundStyle(meta)
                .help(editedTooltip)
        }
    }

    private var editedTooltip: String {
        guard let at = entry.editedAt else { return "This message was edited" }
        let when = Date(timeIntervalSince1970: at).formatted(.dateTime.hour().minute())
        return "Edited at \(when)"
    }

    private var formattedTime: String {
        Date(timeIntervalSince1970: entry.timestamp).formatted(.dateTime.hour().minute())
    }

    private var statusIcon: some View {
        BubbleStatusView(status: entry.status, incoming: entry.incoming)
    }

    // "Delete for Me" is always offered when a delete handler is wired up.
    // "Delete for Everyone" additionally requires a stable messageId and is
    // restricted by the caller to the sender's own outgoing messages.
    @ViewBuilder
    private func deleteMenuItems(allowDeleteForEveryone: Bool) -> some View {
        if onDelete != nil {
            Divider()
            Button(role: .destructive) { onDelete?(false) } label: {
                Label("Delete for Me", systemImage: "trash")
            }
            if allowDeleteForEveryone, canReachPeer, entry.messageId != nil {
                Button(role: .destructive) { onDelete?(true) } label: {
                    Label("Delete for Everyone", systemImage: "trash.fill")
                }
            }
        }
    }

    // Small "via cloud relay" badge shown when a message transited the relay Worker.
    @ViewBuilder
    var relayBadge: some View {
        if entry.deliveryPath == "relay" {
            HStack(spacing: 3) {
                Image(systemName: "cloud")
                    .font(.system(size: 9))
                Text("via relay")
                    .font(GlassTokens.Typography.micro)
            }
            // No opacity: dimming ink-secondary took it below 4.5:1.
            .foregroundStyle(meta)
        }
    }
}
