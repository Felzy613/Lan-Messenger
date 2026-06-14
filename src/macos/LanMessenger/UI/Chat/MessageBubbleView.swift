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
                    onDelete: onDelete
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
        let bg = entry.incoming ? Theme.incomingBubble(colorScheme) : Theme.outgoingBubble(colorScheme)
        return HStack(spacing: 6) {
            Image(systemName: "trash")
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            Text("This message was deleted")
                .font(.system(size: 13))
                .italic()
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .background(bg,
                    in: UnevenRoundedRectangle(
                        topLeadingRadius: 16,
                        bottomLeadingRadius: entry.incoming ? (isFirstInRun ? 4 : 16) : 16,
                        bottomTrailingRadius: entry.incoming ? 16 : (isFirstInRun ? 4 : 16),
                        topTrailingRadius: 16
                    ))
        .frame(maxWidth: 420, alignment: entry.incoming ? .leading : .trailing)
    }

    // MARK: - Reply preview chip (shown at top of bubble when replying)

    @ViewBuilder
    private var replyChip: some View {
        if let preview = entry.replyToPreview, !preview.isEmpty {
            ReplyChipView(
                preview: preview,
                sender: entry.replyToSender,
                filePath: replyFilePath,
                onTap: onTapReplyTarget
            )
        }
    }

    // MARK: - File bubble

    private func fileBubble(path: String) -> some View {
        let url  = URL(fileURLWithPath: path)
        let name = url.lastPathComponent
        let bg = entry.incoming ? Theme.incomingBubble(colorScheme) : Theme.outgoingBubble(colorScheme)

        return VStack(alignment: .leading, spacing: 6) {
            replyChip
            HStack(spacing: 10) {
                Image(systemName: "doc.fill")
                    .font(.system(size: 28))
                    .foregroundStyle(Theme.accent)
                VStack(alignment: .leading, spacing: 3) {
                    Text(name)
                        .font(.system(size: 13, weight: .medium))
                        .lineLimit(2)
                    HStack(spacing: 4) {
                        Text(formattedTime)
                            .font(.system(size: 10))
                            .foregroundStyle(.secondary)
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
                            .font(.system(size: 11, weight: .semibold))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 4)
                            .background(Theme.accent.opacity(0.2), in: Capsule())
                            .foregroundStyle(Theme.accent)
                    }
                    .buttonStyle(.plain)
                    .help("Open \(name) with the default app")

                    Button {
                        FinderReveal.reveal(path: path) { msg in revealError = msg }
                    } label: {
                        Text("Show")
                            .font(.system(size: 11, weight: .semibold))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 4)
                            .background(Color.secondary.opacity(0.15), in: Capsule())
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .help("Show \(name) in Finder")
                } else {
                    Text("Deleted")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .background(bg,
                    in: UnevenRoundedRectangle(
                        topLeadingRadius: 16,
                        bottomLeadingRadius: entry.incoming ? (isFirstInRun ? 4 : 16) : 16,
                        bottomTrailingRadius: entry.incoming ? 16 : (isFirstInRun ? 4 : 16),
                        topTrailingRadius: 16
                    ))
        .frame(maxWidth: 320)
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
            if onDelete != nil {
                Divider()
                Button(role: .destructive) { onDelete?(false) } label: {
                    Label("Delete for Me", systemImage: "trash")
                }
                if !entry.incoming, entry.messageId != nil {
                    Button(role: .destructive) { onDelete?(true) } label: {
                        Label("Delete for Everyone", systemImage: "trash.fill")
                    }
                }
            }
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

    private var incomingBubble: some View {
        VStack(alignment: .leading, spacing: 2) {
            if isFirstInRun {
                Text(entry.sender)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(Theme.accent)
                    .padding(.leading, 4)
            }
            VStack(alignment: .leading, spacing: 4) {
                replyChip
                Text(entry.text)
                    .font(.system(size: 14))
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
                HStack(spacing: 4) {
                    Text(formattedTime)
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                    relayBadge
                }
                .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(Theme.incomingBubble(colorScheme),
                        in: UnevenRoundedRectangle(
                            topLeadingRadius: 16,
                            bottomLeadingRadius: isFirstInRun ? 4 : 16,
                            bottomTrailingRadius: 16,
                            topTrailingRadius: 16
                        ))
        }
        .frame(maxWidth: 420, alignment: .leading)
        .contextMenu {
            if onReply != nil {
                Button { onReply?() } label: { Label("Reply", systemImage: "arrowshape.turn.up.left") }
                Divider()
            }
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(entry.text, forType: .string)
            } label: { Label("Copy", systemImage: "doc.on.doc") }
            if onDelete != nil {
                Divider()
                Button(role: .destructive) { onDelete?(false) } label: {
                    Label("Delete for Me", systemImage: "trash")
                }
            }
        }
    }

    // MARK: - Outgoing text bubble

    private var outgoingBubble: some View {
        VStack(alignment: .trailing, spacing: 4) {
            replyChip
            Text(entry.text)
                .font(.system(size: 14))
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 4) {
                Spacer(minLength: 0)
                Text(formattedTime)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                relayBadge
                statusIcon
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .background(Theme.outgoingBubble(colorScheme),
                    in: UnevenRoundedRectangle(
                        topLeadingRadius: 16,
                        bottomLeadingRadius: 16,
                        bottomTrailingRadius: isFirstInRun ? 4 : 16,
                        topTrailingRadius: 16
                    ))
        .frame(maxWidth: 420, alignment: .trailing)
        .contextMenu {
            if onReply != nil {
                Button { onReply?() } label: { Label("Reply", systemImage: "arrowshape.turn.up.left") }
                Divider()
            }
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(entry.text, forType: .string)
            } label: { Label("Copy", systemImage: "doc.on.doc") }
            if onDelete != nil {
                Divider()
                Button(role: .destructive) { onDelete?(false) } label: {
                    Label("Delete for Me", systemImage: "trash")
                }
                if entry.messageId != nil {
                    Button(role: .destructive) { onDelete?(true) } label: {
                        Label("Delete for Everyone", systemImage: "trash.fill")
                    }
                }
            }
        }
    }

    // MARK: - Helpers

    private var formattedTime: String {
        Date(timeIntervalSince1970: entry.timestamp).formatted(.dateTime.hour().minute())
    }

    private var statusIcon: some View {
        BubbleStatusView(status: entry.status)
    }

    // Small "via cloud relay" badge shown when a message transited the relay Worker.
    @ViewBuilder
    var relayBadge: some View {
        if entry.deliveryPath == "relay" {
            HStack(spacing: 3) {
                Image(systemName: "cloud")
                    .font(.system(size: 9))
                Text("via relay")
                    .font(.system(size: 9))
            }
            .foregroundStyle(.secondary.opacity(0.75))
        }
    }
}
