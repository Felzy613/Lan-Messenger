import SwiftUI
import AppKit
import AVFoundation

// Shared reply chip used by all bubble types.
// Shows a compact thumbnail for image/video replies, a doc icon for file replies,
// and plain text for text replies. Falls back gracefully if the file is missing.
struct ReplyChipView: View {
    let preview: String
    let sender: String?
    // Local absolute path of the replied-to file, if any. Set by ChatView after
    // looking up the original entry by replyToMessageId.
    let filePath: String?
    var onTap: (() -> Void)? = nil

    @State private var thumbnail: NSImage? = nil

    private var mediaKind: MediaKind {
        guard let p = filePath else { return .other }
        return MediaKind.from(path: p)
    }

    private var senderLabel: String {
        (sender?.isEmpty == false) ? sender! : "Reply"
    }

    var body: some View {
        Button { onTap?() } label: { chipContent }
            .buttonStyle(.plain)
            .task(id: filePath) { await loadThumbnail() }
    }

    private var chipContent: some View {
        HStack(spacing: 6) {
            Rectangle().fill(Theme.accent).frame(width: 3)
            if let path = filePath, !path.isEmpty {
                mediaReplyRow(path: path)
            } else {
                textReplyStack
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 4)
        .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 6))
    }

    @ViewBuilder
    private func mediaReplyRow(path: String) -> some View {
        HStack(spacing: 6) {
            // Left: small thumbnail tile or icon
            Group {
                switch mediaKind {
                case .image, .video:
                    if let img = thumbnail {
                        Image(nsImage: img)
                            .resizable()
                            .scaledToFill()
                            .frame(width: 36, height: 36)
                            .clipShape(RoundedRectangle(cornerRadius: 4))
                            .overlay(
                                mediaKind == .video
                                    ? AnyView(
                                        Image(systemName: "play.fill")
                                            .font(.system(size: 11))
                                            .foregroundStyle(.white)
                                            .shadow(radius: 1)
                                    )
                                    : AnyView(EmptyView())
                            )
                    } else {
                        ZStack {
                            RoundedRectangle(cornerRadius: 4)
                                .fill(Color.gray.opacity(0.18))
                                .frame(width: 36, height: 36)
                            Image(systemName: mediaKind == .video ? "play.fill" : "photo")
                                .font(.system(size: 13))
                                .foregroundStyle(.secondary)
                        }
                    }
                case .other:
                    Image(systemName: "doc.fill")
                        .font(.system(size: 20))
                        .foregroundStyle(Theme.accent)
                        .frame(width: 36, height: 36)
                }
            }
            // Right: sender + preview text
            textReplyStack
        }
    }

    private var textReplyStack: some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(senderLabel)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(Theme.accent)
            Text(preview)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
    }

    // MARK: - Async thumbnail load

    private func loadThumbnail() async {
        guard let path = filePath, !path.isEmpty else { return }
        guard mediaKind == .image || mediaKind == .video else { return }
        guard FileManager.default.fileExists(atPath: path) else { return }

        // Check the shared cache first; chips use a "_chip36" suffix to keep their
        // 36-pt square crop separate from the full-resolution media bubble thumbnails.
        let cacheKey = path + "_chip36"
        if let cached = ThumbnailCache.shared.thumbnail(for: cacheKey) {
            thumbnail = cached
            return
        }

        let k = mediaKind
        let result = await Task.detached(priority: .utility) { () -> NSImage? in
            let raw: NSImage?
            switch k {
            case .image:
                raw = NSImage(contentsOfFile: path)
            case .video:
                let asset = AVURLAsset(url: URL(fileURLWithPath: path))
                let gen   = AVAssetImageGenerator(asset: asset)
                gen.appliesPreferredTrackTransform = true
                gen.maximumSize = CGSize(width: 72, height: 72)
                let t = CMTime(seconds: 0.5, preferredTimescale: 600)
                if let cg = try? gen.copyCGImage(at: t, actualTime: nil) {
                    raw = NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
                } else {
                    raw = nil
                }
            default:
                raw = nil
            }
            guard let raw else { return nil }

            // Centre-crop to 36 × 36 pt.
            let side: CGFloat = 36
            let out  = NSImage(size: CGSize(width: side, height: side))
            out.lockFocus()
            NSGraphicsContext.current?.imageInterpolation = .high
            let scale = max(side / raw.size.width, side / raw.size.height)
            let srcW  = side / scale
            let srcH  = side / scale
            let srcX  = (raw.size.width  - srcW) / 2
            let srcY  = (raw.size.height - srcH) / 2
            raw.draw(in: NSRect(origin: .zero, size: CGSize(width: side, height: side)),
                     from: NSRect(x: srcX, y: srcY, width: srcW, height: srcH),
                     operation: .copy, fraction: 1.0)
            out.unlockFocus()
            return out
        }.value

        if let result {
            ThumbnailCache.shared.store(result, for: cacheKey)
            thumbnail = result
        }
    }
}
