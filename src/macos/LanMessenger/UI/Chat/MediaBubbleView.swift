import SwiftUI
import AppKit
import AVKit
import AVFoundation

// Inline image / video bubble rendered for received or sent media files.
// Falls back to the regular file bubble when the file is missing on disk.
//
// Design notes:
//   • Thumbnails (images and video first-frame) are loaded asynchronously off the
//     main thread using `Task.detached(priority: .utility)`. They are cached in
//     memory by absolute path so re-rendering during scroll does not re-decode.
//   • Video bubbles do NOT instantiate AVPlayer up front. We show a poster + play
//     button; AVPlayer is created only when the user taps to open the modal
//     viewer. This keeps the chat list cheap with many video messages.
//   • The bubble width is capped to 280 pt and height to 320 pt so a single huge
//     image cannot dominate the message list. Aspect ratio is preserved.
//   • Tapping the bubble opens a modal sheet with the full-size image (zoomable
//     via NSImageView's built-in pan/scroll) or a full AVPlayer with controls.
//   • All NSImage allocation happens on the utility queue. We never touch
//     SwiftUI from those tasks; results are published to @State via MainActor.

struct MediaBubbleView: View {
    let entry: MessageEntry
    let isFirstInRun: Bool
    let kind: MediaKind                          // .image or .video — caller must filter
    var onReply: (() -> Void)? = nil
    var onTapReplyTarget: (() -> Void)? = nil
    @Environment(\.colorScheme) var colorScheme

    @State private var thumbnail: NSImage? = nil
    @State private var loadFailed = false
    @State private var fileExists = false
    @State private var showPreview = false
    @State private var revealError: String? = nil

    private var path: String {
        // Same convention as MessageBubbleView — "__FILE__:" prefix on entry.text.
        entry.text.hasPrefix("__FILE__:")
            ? String(entry.text.dropFirst("__FILE__:".count))
            : entry.text
    }

    private var url: URL { URL(fileURLWithPath: path) }
    private var filename: String { url.lastPathComponent }

    private var bubbleBackground: Color {
        entry.incoming ? Theme.incomingBubble(colorScheme) : Theme.outgoingBubble(colorScheme)
    }

    // Cap the inline rendering so a 4K image doesn't take over the chat list.
    private let maxBubbleWidth: CGFloat = 280
    private let maxBubbleHeight: CGFloat = 320

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
        .task(id: path) {
            await refreshFileState()
        }
        .sheet(isPresented: $showPreview) {
            MediaPreviewSheet(url: url, kind: kind, filename: filename)
        }
        .alert("Cannot open file location",
               isPresented: Binding(get: { revealError != nil },
                                    set: { if !$0 { revealError = nil } })) {
            Button("OK", role: .cancel) { }
        } message: {
            Text(revealError ?? "")
        }
    }

    @ViewBuilder
    private var bubble: some View {
        if !fileExists {
            // File got deleted/moved between history append and render — fall
            // back to the lightweight "missing" placeholder.  We deliberately
            // do NOT render a placeholder image here because that would mask
            // the deletion from the user.
            missingBubble
        } else {
            VStack(alignment: .leading, spacing: 0) {
                replyChipIfNeeded
                mediaTile
                footer
            }
            .padding(6)
            .background(bubbleBackground,
                        in: UnevenRoundedRectangle(
                            topLeadingRadius: 16,
                            bottomLeadingRadius: entry.incoming ? (isFirstInRun ? 4 : 16) : 16,
                            bottomTrailingRadius: entry.incoming ? 16 : (isFirstInRun ? 4 : 16),
                            topTrailingRadius: 16
                        ))
            .frame(maxWidth: maxBubbleWidth + 12)
            .contextMenu { bubbleContextMenu }
        }
    }

    // MARK: - Inner pieces

    @ViewBuilder
    private var replyChipIfNeeded: some View {
        if let preview = entry.replyToPreview, !preview.isEmpty {
            HStack(spacing: 6) {
                Rectangle().fill(Theme.accent).frame(width: 3)
                VStack(alignment: .leading, spacing: 1) {
                    Text(entry.replyToSender?.isEmpty == false ? entry.replyToSender! : "Reply")
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
            .padding(.bottom, 4)
        }
    }

    @ViewBuilder
    private var mediaTile: some View {
        Button {
            showPreview = true
        } label: {
            ZStack(alignment: .bottomTrailing) {
                tileContent
                    .frame(maxWidth: maxBubbleWidth, maxHeight: maxBubbleHeight)
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                if kind == .video {
                    Image(systemName: "play.circle.fill")
                        .font(.system(size: 36))
                        .foregroundStyle(.white)
                        .shadow(radius: 2)
                        .padding(10)
                        .accessibilityHidden(true)
                }
            }
        }
        .buttonStyle(MediaTilePressStyle())
        .help(kind == .video ? "Play \(filename)" : "Open \(filename)")
    }

    @ViewBuilder
    private var tileContent: some View {
        if let img = thumbnail {
            Image(nsImage: img)
                .resizable()
                .aspectRatio(contentMode: .fit)
        } else if loadFailed {
            // Thumbnail decode failed but file exists — render a neutral tile
            // rather than a crash-prone empty image.
            placeholderTile(systemImage: kind == .video ? "video" : "photo",
                            label: filename)
        } else {
            placeholderTile(systemImage: kind == .video ? "video" : "photo",
                            label: "")
                .overlay(ProgressView().controlSize(.small))
        }
    }

    @ViewBuilder
    private func placeholderTile(systemImage: String, label: String) -> some View {
        ZStack {
            Rectangle()
                .fill(Color.gray.opacity(0.18))
            VStack(spacing: 6) {
                Image(systemName: systemImage)
                    .font(.system(size: 28))
                    .foregroundStyle(.secondary)
                if !label.isEmpty {
                    Text(label)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            .padding(8)
        }
        .frame(width: 220, height: 160)
    }

    private var footer: some View {
        HStack(spacing: 4) {
            Text(filename)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .lineLimit(1)
            Spacer(minLength: 4)
            Text(formattedTime)
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
            if !entry.incoming { statusIcon }
        }
        .padding(.horizontal, 4)
        .padding(.top, 6)
    }

    @ViewBuilder
    private var bubbleContextMenu: some View {
        if onReply != nil {
            Button { onReply?() } label: { Label("Reply", systemImage: "arrowshape.turn.up.left") }
            Divider()
        }
        Button {
            FinderReveal.reveal(path: path) { msg in revealError = msg }
        } label: { Label("Show in Finder", systemImage: "folder") }
        Button {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(path, forType: .string)
        } label: { Label("Copy Path", systemImage: "doc.on.doc") }
        Button {
            NSWorkspace.shared.open(url)
        } label: { Label("Open", systemImage: "square.and.arrow.up") }
    }

    private var missingBubble: some View {
        HStack(spacing: 10) {
            Image(systemName: kind == .video ? "video.slash" : "photo.badge.exclamationmark")
                .font(.system(size: 22))
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(filename)
                    .font(.system(size: 13, weight: .medium))
                    .lineLimit(1)
                Text("File no longer available")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .background(bubbleBackground, in: RoundedRectangle(cornerRadius: 14))
        .frame(maxWidth: 320)
    }

    // MARK: - Helpers

    private var formattedTime: String {
        Date(timeIntervalSince1970: entry.timestamp).formatted(.dateTime.hour().minute())
    }

    @ViewBuilder
    private var statusIcon: some View {
        // Re-use the same status icons defined in MessageBubbleView. Keep them
        // visually consistent so file/media/text bubbles all read the same way.
        switch entry.status {
        case "Read":
            doubleCheck(color: Color(red: 0.31, green: 0.62, blue: 0.97))
        case "Delivered":
            doubleCheck(color: .secondary)
        case "Sent":
            Image(systemName: "checkmark")
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(.secondary)
        case "Queued", "Sending":
            Image(systemName: "clock").font(.system(size: 10)).foregroundStyle(.secondary)
        case "Failed":
            Image(systemName: "exclamationmark.circle").font(.system(size: 10)).foregroundStyle(.red)
        default:
            EmptyView()
        }
    }

    private func doubleCheck(color: Color) -> some View {
        ZStack(alignment: .leading) {
            Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).offset(x: 0)
            Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).offset(x: 4)
        }
        .frame(width: 14, height: 10, alignment: .leading)
        .foregroundStyle(color)
    }

    // MARK: - Async work

    private func refreshFileState() async {
        let pathCopy = path
        let kindCopy = kind
        let (exists, image) = await Task.detached(priority: .utility) { () -> (Bool, NSImage?) in
            let exists = FileManager.default.fileExists(atPath: pathCopy)
            guard exists else { return (false, nil) }
            // Try the cache first.
            if let cached = ThumbnailCache.shared.thumbnail(for: pathCopy) {
                return (true, cached)
            }
            let img: NSImage?
            switch kindCopy {
            case .image:
                img = NSImage(contentsOfFile: pathCopy)
            case .video:
                img = Self.makeVideoThumbnail(path: pathCopy)
            case .other:
                img = nil
            }
            if let img { ThumbnailCache.shared.store(img, for: pathCopy) }
            return (true, img)
        }.value

        await MainActor.run {
            self.fileExists = exists
            self.thumbnail = image
            self.loadFailed = exists && image == nil
        }
    }

    /// Generate a representative still for a video file. Runs on a background queue.
    nonisolated private static func makeVideoThumbnail(path: String) -> NSImage? {
        let asset = AVURLAsset(url: URL(fileURLWithPath: path))
        let gen = AVAssetImageGenerator(asset: asset)
        gen.appliesPreferredTrackTransform = true
        gen.maximumSize = CGSize(width: 640, height: 640)
        let time = CMTime(seconds: 0.5, preferredTimescale: 600)
        do {
            let cg = try gen.copyCGImage(at: time, actualTime: nil)
            return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
        } catch {
            NetLogger.warn("MediaBubble", "video thumbnail failed for \(path): \(error.localizedDescription)")
            return nil
        }
    }
}

// MARK: - Press style

private struct MediaTilePressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .opacity(configuration.isPressed ? 0.85 : 1.0)
            .scaleEffect(configuration.isPressed ? 0.99 : 1.0)
            .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
    }
}

// MARK: - Modal preview sheet

struct MediaPreviewSheet: View {
    let url: URL
    let kind: MediaKind
    let filename: String

    @Environment(\.dismiss) private var dismiss
    @State private var image: NSImage? = nil
    @State private var player: AVPlayer? = nil

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(filename)
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(1)
                Spacer()
                Button {
                    FinderReveal.reveal(path: url.path) { _ in /* swallow — modal is closing */ }
                } label: {
                    Label("Show in Finder", systemImage: "folder")
                }
                Button("Close") { dismiss() }
                    .keyboardShortcut(.escape, modifiers: [])
            }
            .padding(10)
            .background(.bar)
            Divider()
            Group {
                switch kind {
                case .image:
                    if let img = image {
                        ScrollView([.horizontal, .vertical]) {
                            Image(nsImage: img)
                                .resizable()
                                .scaledToFit()
                                .frame(maxWidth: .infinity, maxHeight: .infinity)
                        }
                    } else {
                        ProgressView().controlSize(.large)
                    }
                case .video:
                    if let player = player {
                        VideoPlayer(player: player)
                            .onAppear { player.play() }
                            .onDisappear { player.pause() }
                    } else {
                        ProgressView().controlSize(.large)
                    }
                case .other:
                    Text("Cannot preview file").foregroundStyle(.secondary)
                }
            }
            .frame(minWidth: 480, minHeight: 360)
            .background(Color.black.opacity(0.9))
        }
        .frame(minWidth: 600, minHeight: 460)
        .task(id: url.path) {
            switch kind {
            case .image:
                let p = url.path
                let img = await Task.detached(priority: .userInitiated) {
                    NSImage(contentsOfFile: p)
                }.value
                self.image = img
            case .video:
                self.player = AVPlayer(url: url)
            case .other:
                break
            }
        }
    }
}

// MARK: - Thumbnail cache (in-memory, NSCache-backed)

/// Memory-bounded cache for decoded NSImages keyed by absolute file path.
/// NSCache evicts under memory pressure. The cache is intentionally process-local;
/// we do not persist thumbnails to disk because the saved files themselves are the
/// canonical source and re-decoding on relaunch is cheap.
final class ThumbnailCache {
    static let shared = ThumbnailCache()

    private let cache = NSCache<NSString, NSImage>()

    private init() {
        // Cap roughly the memory cost of ~64 medium thumbnails (1024×1024 RGBA).
        cache.totalCostLimit = 64 * 4 * 1024 * 1024
        cache.countLimit = 256
    }

    func thumbnail(for path: String) -> NSImage? {
        cache.object(forKey: path as NSString)
    }

    func store(_ image: NSImage, for path: String) {
        // Cost estimate — pixel count × 4 bytes per pixel. NSImage size is in points,
        // but multiplied by representation scale where available.
        let pixels = image.representations.reduce(into: 0) { acc, rep in
            acc += rep.pixelsWide * rep.pixelsHigh
        }
        let cost = max(pixels * 4, Int(image.size.width * image.size.height * 4))
        cache.setObject(image, forKey: path as NSString, cost: cost)
    }
}
