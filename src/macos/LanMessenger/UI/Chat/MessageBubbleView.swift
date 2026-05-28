import SwiftUI
import AppKit

struct MessageBubbleView: View {
    let entry: MessageEntry
    let isFirstInRun: Bool
    var onReply: (() -> Void)? = nil
    var onTapReplyTarget: (() -> Void)? = nil
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
        if let path = filePath {
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
                    onTapReplyTarget: onTapReplyTarget
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

    // MARK: - Reply preview chip (shown at top of bubble when replying)

    @ViewBuilder
    private var replyChip: some View {
        if let preview = entry.replyToPreview, !preview.isEmpty {
            let label = entry.replyToSender?.isEmpty == false ? entry.replyToSender! : "Reply"
            Button {
                onTapReplyTarget?()
            } label: {
                HStack(spacing: 6) {
                    Rectangle()
                        .fill(Theme.accent)
                        .frame(width: 3)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(label)
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(Theme.accent)
                        Text(preview)
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, 6)
                .padding(.vertical, 4)
                .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
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
                        if !entry.incoming { statusIcon }
                    }
                }
                Spacer(minLength: 0)
                if fileExists {
                    // "Open" hands the file to its default app via
                    // NSWorkspace.open (async, non-blocking). "Show" reveals
                    // it in Finder. Both work off the main thread so launching
                    // an external app can't stutter the chat list.
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
                            .background(Theme.accent.opacity(0.12), in: Capsule())
                            .foregroundStyle(Theme.accent)
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
                Text(formattedTime)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
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
        }
    }

    // MARK: - Helpers

    private var formattedTime: String {
        Date(timeIntervalSince1970: entry.timestamp).formatted(.dateTime.hour().minute())
    }

    // WhatsApp-style checkmarks:
    // - Sending/Queued/Sent → single grey check
    // - Delivered           → double grey check
    // - Read                → double blue check
    // - Failed              → red exclamation
    //
    // In-flight states (Sending, Queued) deliberately render the same single
    // grey check as Sent — the user never sees a clock or other transient
    // glyph, so the lifecycle reads cleanly as ✓ → ✓✓ grey → ✓✓ blue.
    @ViewBuilder
    private var statusIcon: some View {
        switch entry.status {
        case "Read":
            doubleCheck(color: Color(red: 0.31, green: 0.62, blue: 0.97))   // WhatsApp-ish blue
        case "Delivered":
            doubleCheck(color: .secondary)
        case "Failed":
            Image(systemName: "exclamationmark.circle")
                .font(.system(size: 10))
                .foregroundStyle(.red)
        default:
            // Sent, Sending, Queued, and unknown in-flight states all share
            // the single grey check.
            Image(systemName: "checkmark")
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(.secondary)
        }
    }

    private func doubleCheck(color: Color) -> some View {
        // Two overlapping checkmarks for the "delivered" look.
        ZStack(alignment: .leading) {
            Image(systemName: "checkmark")
                .font(.system(size: 10, weight: .bold))
                .offset(x: 0)
            Image(systemName: "checkmark")
                .font(.system(size: 10, weight: .bold))
                .offset(x: 4)
        }
        .frame(width: 14, height: 10, alignment: .leading)
        .foregroundStyle(color)
    }
}
