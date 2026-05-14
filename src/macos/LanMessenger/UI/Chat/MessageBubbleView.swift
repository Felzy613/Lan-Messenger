import SwiftUI
import AppKit

struct MessageBubbleView: View {
    let entry: MessageEntry
    let isFirstInRun: Bool
    @Environment(\.colorScheme) var colorScheme

    // File messages use a "__FILE__:/path/to/file" prefix stored by AppModel.
    // Works for both incoming (sender="System") and outgoing (sender=username) entries.
    private var filePath: String? {
        entry.text.hasPrefix("__FILE__:")
            ? String(entry.text.dropFirst("__FILE__:".count))
            : nil
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
            fileBubble(path: path)
        } else if entry.incoming {
            incomingBubble
        } else {
            outgoingBubble
        }
    }

    // MARK: - File bubble

    private func fileBubble(path: String) -> some View {
        let url  = URL(fileURLWithPath: path)
        let name = url.lastPathComponent
        let exists = FileManager.default.fileExists(atPath: path)
        let bg = entry.incoming ? Theme.incomingBubble(colorScheme) : Theme.outgoingBubble(colorScheme)

        return HStack(spacing: 10) {
            Image(systemName: "doc.fill")
                .font(.system(size: 28))
                .foregroundStyle(Theme.accent)
            VStack(alignment: .leading, spacing: 3) {
                Text(name)
                    .font(.system(size: 13, weight: .medium))
                    .lineLimit(2)
                Text(formattedTime)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
            if exists {
                Button {
                    NSWorkspace.shared.selectFile(path, inFileViewerRootedAtPath: "")
                } label: {
                    Text("Show")
                        .font(.system(size: 11, weight: .semibold))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(Theme.accent.opacity(0.2), in: Capsule())
                        .foregroundStyle(Theme.accent)
                }
                .buttonStyle(.plain)
            } else {
                Text("Deleted")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
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
        .frame(maxWidth: 280)
        .contextMenu {
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(path, forType: .string)
            } label: { Label("Copy Path", systemImage: "doc.on.doc") }
            if exists {
                Button {
                    NSWorkspace.shared.selectFile(path, inFileViewerRootedAtPath: "")
                } label: { Label("Show in Finder", systemImage: "folder") }
            }
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
                Text(entry.text)
                    .font(.system(size: 14))
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
                Text(formattedTime)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
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
        .contextMenu {
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(entry.text, forType: .string)
            } label: { Label("Copy", systemImage: "doc.on.doc") }
        }
    }

    // MARK: - Outgoing text bubble

    private var outgoingBubble: some View {
        VStack(alignment: .trailing, spacing: 4) {
            Text(entry.text)
                .font(.system(size: 14))
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
            HStack(spacing: 3) {
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
        .contextMenu {
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

    @ViewBuilder
    private var statusIcon: some View {
        switch entry.status {
        case "Read":
            Image(systemName: "checkmark.bubble.fill")
                .font(.system(size: 10))
                .foregroundStyle(Theme.accent)
        case "Sent":
            Image(systemName: "checkmark")
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
        case "Queued", "Sending":
            Image(systemName: "clock")
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
        case "Failed":
            Image(systemName: "exclamationmark.circle")
                .font(.system(size: 10))
                .foregroundStyle(.red)
        default:
            EmptyView()
        }
    }
}
