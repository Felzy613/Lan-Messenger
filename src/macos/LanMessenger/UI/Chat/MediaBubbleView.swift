import SwiftUI
import AppKit
import AVKit
import AVFoundation
import ImageIO

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
    /// Local file path of the replied-to message (if it was a media/file message).
    var replyFilePath: String? = nil
    /// Called when the user chooses a delete option from the context menu.
    /// The Bool is `forEveryone` — true for "Delete for Everyone", false for "Delete for Me".
    var onDelete: ((Bool) -> Void)? = nil
    /// False in a legacy thread, which has no identity key behind it:
    /// "Delete for Everyone" would have nobody to tell.
    var canReachPeer: Bool = true
    @Environment(\.colorScheme) var colorScheme

    @State private var thumbnail: NSImage? = nil
    // Retained while the preview panel is on screen; released on close.
    @State private var previewPanel: NSPanel? = nil
    /// Pre-computed display frame for the loaded thumbnail (pt), used to give
    /// SwiftUI a fixed layout size so it never needs to solve aspect-ratio
    /// equations during a LazyVStack/VStack layout pass.
    @State private var thumbnailDisplaySize: CGSize = CGSize(width: 220, height: 160)
    /// Natural pixel dimensions of the image file (nil for videos/other or until loaded).
    @State private var naturalImageSize: CGSize? = nil
    @State private var loadFailed = false
    @State private var fileExists = false
    @State private var revealError: String? = nil

    private var path: String {
        // Same convention as MessageBubbleView — "__FILE__:" prefix on entry.text.
        entry.text.hasPrefix("__FILE__:")
            ? String(entry.text.dropFirst("__FILE__:".count))
            : entry.text
    }

    private var url: URL { URL(fileURLWithPath: path) }
    private var filename: String { url.lastPathComponent }

    /// The bubble's meta colour: time, file name, ticks.
    private var meta: Color { Theme.meta(incoming: entry.incoming) }

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
            .padding(4)
            .bubbleSurface(incoming: entry.incoming, tail: isFirstInRun)
            .frame(maxWidth: maxBubbleWidth + 12)
            .contextMenu { bubbleContextMenu }
            // Drag the bubble straight out to Finder, Mail, or any app that
            // takes a file. NSItemProvider(contentsOf:) hands over the real
            // file, so the receiving app copies the attachment rather than
            // getting a path string.
            //
            // Must sit OUTSIDE .contextMenu: applied the other way round the
            // context-menu wrapper owns the mouse-down and the drag never
            // starts.
            .onDrag { AttachmentPasteboard.outgoingProvider(forFileAt: path) ?? NSItemProvider() }
        }
    }

    // MARK: - Inner pieces

    @ViewBuilder
    private var replyChipIfNeeded: some View {
        if let preview = entry.replyToPreview, !preview.isEmpty {
            ReplyChipView(
                preview: preview,
                sender: entry.replyToSender,
                filePath: replyFilePath,
                incoming: entry.incoming,
                onTap: onTapReplyTarget
            )
            .padding([.horizontal, .top], 2)
            .padding(.bottom, 4)
        }
    }

    @ViewBuilder
    private var mediaTile: some View {
        // Deliberately a tap gesture rather than a Button: a Button's press
        // gesture claims the mouse-down and the drag that follows never reaches
        // the .onDrag below it, so the bubble could not be dragged out at all.
        // onTapGesture composes with the drag — click opens, movement drags.
        Group {
            ZStack(alignment: .bottomTrailing) {
                // tileContent already carries an explicit fixed frame (either the
                // pre-computed thumbnailDisplaySize for loaded images, or the
                // 220×160 fixed frame from placeholderTile).  No max-width/height
                // constraint needed here — removing it eliminates the layout
                // ambiguity that caused the hang-report AG cycle.
                // radius-media: 16 - 4 inset, concentric with the bubble.
                tileContent
                    .clipShape(RoundedRectangle(cornerRadius: GlassTokens.Radius.media))
                if kind == .video {
                    // A 48pt play disc on scrim: legible over any frame.
                    ZStack {
                        Circle().fill(GlassTokens.scrim)
                        Image(systemName: "play.fill")
                            .font(.system(size: 18, weight: .semibold))
                            .foregroundStyle(GlassTokens.inkInverse)
                            .offset(x: 1)
                    }
                    .frame(width: 48, height: 48)
                    .padding(10)
                    .accessibilityHidden(true)
                }
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { openPreviewPanel() }
        .help(kind == .video ? "Play \(filename)" : "Open \(filename)")
    }

    @ViewBuilder
    private var tileContent: some View {
        if let img = thumbnail {
            // Use a fixed frame derived from the pre-scaled thumbnail dimensions.
            // This gives SwiftUI a concrete size during layout so it never needs
            // to solve an aspect-ratio equation for an unconstrained Image — the
            // pattern that caused the main-thread hang (AG layout cycle).
            Image(nsImage: img)
                .resizable()
                .frame(width: thumbnailDisplaySize.width, height: thumbnailDisplaySize.height)
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
                .fill(Theme.insetFill)
            VStack(spacing: 6) {
                Image(systemName: systemImage)
                    .font(.system(size: 28))
                    .foregroundStyle(meta)
                if !label.isEmpty {
                    Text(label)
                        .font(GlassTokens.Typography.caption)
                        .foregroundStyle(meta)
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
                .font(GlassTokens.Typography.caption)
                .foregroundStyle(meta)
                .lineLimit(1)
            Spacer(minLength: 4)
            Text(formattedTime)
                .font(GlassTokens.Typography.meta)
                .foregroundStyle(meta)
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
        deleteMenuItems(allowDeleteForEveryone: !entry.incoming)
    }

    private var missingBubble: some View {
        HStack(spacing: 10) {
            Image(systemName: kind == .video ? "video.slash" : "photo.badge.exclamationmark")
                .font(.system(size: 22))
                .foregroundStyle(meta)
            VStack(alignment: .leading, spacing: 2) {
                Text(filename)
                    .font(GlassTokens.Typography.fileName)
                    .foregroundStyle(Theme.ink)
                    .lineLimit(1)
                Text("File no longer available")
                    .font(GlassTokens.Typography.caption)
                    .foregroundStyle(meta)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .bubbleSurface(incoming: entry.incoming, tail: isFirstInRun)
        .frame(maxWidth: GlassTokens.Size.fileBubbleMax)
    }

    // MARK: - Preview panel

    @MainActor
    private func openPreviewPanel() {
        let size = MediaPreviewSheet.frameSize(naturalSize: naturalImageSize, kind: kind)
        let panel = EscapablePanel(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.titled, .closable, .resizable],
            backing: .buffered,
            defer: false
        )
        panel.title = filename
        panel.center()
        panel.contentView = NSHostingView(rootView: MediaPreviewSheet(
            url: url,
            kind: kind,
            filename: filename,
            naturalSize: naturalImageSize,
            onClose: { [weak panel] in panel?.close() }
        ))
        panel.makeKeyAndOrderFront(nil)
        previewPanel = panel
    }

    // MARK: - Helpers

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

    // MARK: - Async work

    private func refreshFileState() async {
        let pathCopy = path
        let kindCopy = kind
        let maxW = maxBubbleWidth
        let maxH = maxBubbleHeight
        let (exists, image, displaySize, naturalSz) = await Task.detached(priority: .utility) {
            () -> (Bool, NSImage?, CGSize, CGSize) in
            let exists = FileManager.default.fileExists(atPath: pathCopy)
            guard exists else { return (false, nil, .zero, .zero) }
            let natSz: CGSize = kindCopy == .image ? Self.naturalImagePixelSize(at: pathCopy) : .zero
            // Try the cache first.
            if let cached = ThumbnailCache.shared.thumbnail(for: pathCopy) {
                let sz = Self.fitSize(natural: cached.size, maxWidth: maxW, maxHeight: maxH)
                return (true, cached, sz, natSz)
            }
            let raw: NSImage?
            switch kindCopy {
            case .image:
                raw = NSImage(contentsOfFile: pathCopy)
            case .video:
                raw = Self.makeVideoThumbnail(path: pathCopy)
            case .other:
                raw = nil
            }
            guard let raw else { return (true, nil, .zero, natSz) }
            // Scale down to the maximum bubble display dimensions (pt) before
            // caching and returning.  Storing a down-sampled image means SwiftUI
            // always gets a small, fixed-dimension NSImage — preventing the
            // aspect-ratio layout ambiguity that caused the main-thread hang.
            let displaySz = Self.fitSize(natural: raw.size, maxWidth: maxW, maxHeight: maxH)
            let scaled = Self.scale(image: raw, to: displaySz)
            ThumbnailCache.shared.store(scaled, for: pathCopy)
            return (true, scaled, displaySz, natSz)
        }.value

        await MainActor.run {
            self.fileExists = exists
            self.thumbnail = image
            self.thumbnailDisplaySize = (image != nil) ? displaySize : CGSize(width: 220, height: 160)
            self.loadFailed = exists && image == nil
            self.naturalImageSize = (naturalSz.width > 0 && naturalSz.height > 0) ? naturalSz : nil
        }
    }

    /// Compute the largest size that fits `natural` within `maxWidth × maxHeight`
    /// while preserving the aspect ratio.  Returns the placeholder size when
    /// the natural size is zero.
    nonisolated private static func fitSize(natural: CGSize,
                                            maxWidth: CGFloat,
                                            maxHeight: CGFloat) -> CGSize {
        guard natural.width > 0, natural.height > 0 else {
            return CGSize(width: 220, height: 160)
        }
        let widthRatio  = maxWidth  / natural.width
        let heightRatio = maxHeight / natural.height
        let scale       = min(widthRatio, heightRatio, 1.0)   // never upscale
        return CGSize(width: (natural.width  * scale).rounded(),
                      height: (natural.height * scale).rounded())
    }

    /// Redraw `image` at exactly `targetSize` using Core Graphics.
    nonisolated private static func scale(image: NSImage, to targetSize: CGSize) -> NSImage {
        guard targetSize.width > 0, targetSize.height > 0 else { return image }
        // If the image is already at or below the target resolution, skip redraw.
        if image.size.width <= targetSize.width && image.size.height <= targetSize.height {
            return image
        }
        let result = NSImage(size: targetSize)
        result.lockFocus()
        NSGraphicsContext.current?.imageInterpolation = .high
        image.draw(in: NSRect(origin: .zero, size: targetSize),
                   from: .zero, operation: .copy, fraction: 1.0)
        result.unlockFocus()
        return result
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

    /// Read image pixel dimensions from the file header without decoding pixel data.
    /// CGImageSource property values are CFNumber/NSNumber (integer), not CGFloat,
    /// so we cast via Int to avoid the silent nil that `as? CGFloat` produces.
    nonisolated private static func naturalImagePixelSize(at path: String) -> CGSize {
        let url  = URL(fileURLWithPath: path) as CFURL
        let opts = [kCGImageSourceShouldCache: false] as CFDictionary
        guard let src   = CGImageSourceCreateWithURL(url, opts),
              let props = CGImageSourceCopyPropertiesAtIndex(src, 0, opts) as? [CFString: Any],
              let pw    = props[kCGImagePropertyPixelWidth]  as? Int,
              let ph    = props[kCGImagePropertyPixelHeight] as? Int
        else { return .zero }
        return CGSize(width: CGFloat(pw), height: CGFloat(ph))
    }
}

// MARK: - Modal preview sheet

struct MediaPreviewSheet: View {
    let url: URL
    let kind: MediaKind
    let filename: String
    var naturalSize: CGSize? = nil
    var onClose: (() -> Void)? = nil

    @State private var image: NSImage? = nil
    @State private var player: AVPlayer? = nil

    /// Window size based on the image's natural pixel dimensions.
    /// Caps at the visible screen area with an 80 pt margin on each axis.
    static func frameSize(naturalSize: CGSize?, kind: MediaKind) -> CGSize {
        guard kind == .image,
              let nat = naturalSize, nat.width > 0, nat.height > 0
        else {
            return CGSize(width: 1000, height: 720)
        }
        let screen  = NSScreen.main?.visibleFrame.size ?? CGSize(width: 1440, height: 900)
        let headerH: CGFloat = 44
        let maxW    = max(400, screen.width  - 80)
        let maxH    = max(300, screen.height - 80 - headerH)
        let scale   = min(maxW / nat.width, maxH / nat.height, 1.0)
        return CGSize(
            width:  max(400, (nat.width  * scale).rounded()),
            height: max(300, (nat.height * scale).rounded()) + headerH
        )
    }

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
                Button("Close") { onClose?() }
                    .keyboardShortcut(.escape, modifiers: [])
            }
            .padding(10)
            .background(.bar)
            Divider()
            Group {
                switch kind {
                case .image:
                    if let img = image {
                        // ZoomableImageView fits the image to the viewer while
                        // preserving aspect ratio (no scrollbars for normal-sized
                        // images), supports scroll/pinch zoom for large images,
                        // and resizes with the window.
                        ZoomableImageView(image: img)
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
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color.black.opacity(0.95))
        }
        .task(id: url.path) {
            switch kind {
            case .image:
                let p = url.path
                let img: NSImage? = await Task.detached(priority: .userInitiated) {
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

// MARK: - Zoomable image view (full-screen preview)

/// Full-resolution image viewer for the preview sheet.
///
/// Backed by AppKit's `NSScrollView` + `NSImageView` (wrapped in
/// `NSViewRepresentable`, the same pattern as `ComposerTextEditor`) rather than
/// pure-SwiftUI gestures. AppKit already implements the entire fit / zoom / pan
/// story:
///   • `NSImageView.imageScaling = .scaleProportionallyUpOrDown` + `.alignCenter`
///     letter-boxes the image to fit the viewer while preserving aspect ratio —
///     no scrollbars appear for normal-sized images.
///   • `NSScrollView.allowsMagnification` gives smooth scroll/pinch zoom for
///     images larger than the window (1×–8×).
///   • The image view auto-resizes with the clip view, so the picture reflows
///     whenever the window is resized.
struct ZoomableImageView: NSViewRepresentable {
    let image: NSImage

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = false
        scroll.hasHorizontalScroller = false
        scroll.borderType = .noBorder
        scroll.drawsBackground = false
        scroll.allowsMagnification = true
        scroll.minMagnification = 1.0
        scroll.maxMagnification = 8.0
        scroll.autohidesScrollers = true

        let imageView = NSImageView()
        imageView.imageScaling = .scaleProportionallyUpOrDown
        imageView.imageAlignment = .alignCenter
        imageView.image = image
        imageView.frame = scroll.bounds
        imageView.autoresizingMask = [.width, .height]

        scroll.documentView = imageView
        return scroll
    }

    func updateNSView(_ nsView: NSScrollView, context: Context) {
        guard let imageView = nsView.documentView as? NSImageView else { return }
        imageView.image = image
    }
}

// MARK: - Escapable panel

private final class EscapablePanel: NSPanel {
    override func cancelOperation(_ sender: Any?) { close() }
}

// MARK: - Thumbnail cache (in-memory, NSCache-backed)

/// Memory-bounded cache for decoded NSImages, keyed by absolute file path *and*
/// the file's content version (modification date + size).
/// NSCache evicts under memory pressure. The cache is intentionally process-local;
/// we do not persist thumbnails to disk because the saved files themselves are the
/// canonical source and re-decoding on relaunch is cheap.
///
/// The content version is not optional.  A chat bubble stores only an absolute
/// path, so when a file is overwritten in place — re-exporting an image under
/// the same name and sending it again is the ordinary case — a path-only key
/// keeps serving the thumbnail decoded from the previous contents, and the
/// bubble silently disagrees with the bytes that were actually transmitted.
final class ThumbnailCache {
    static let shared = ThumbnailCache()

    private let cache = NSCache<NSString, NSImage>()

    private init() {
        // Cap roughly the memory cost of ~64 medium thumbnails (1024×1024 RGBA).
        cache.totalCostLimit = 64 * 4 * 1024 * 1024
        cache.countLimit = 256
    }

    /// `variant` separates differently-sized renderings of the same file
    /// (e.g. the 36-pt reply chip crop vs. the full bubble thumbnail).
    private func key(for path: String, variant: String) -> NSString {
        let attrs = try? FileManager.default.attributesOfItem(atPath: path)
        let mtime = (attrs?[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
        let size  = (attrs?[.size] as? NSNumber)?.int64Value ?? 0
        return "\(path)|\(variant)|\(mtime)|\(size)" as NSString
    }

    func thumbnail(for path: String, variant: String = "") -> NSImage? {
        cache.object(forKey: key(for: path, variant: variant))
    }

    func store(_ image: NSImage, for path: String, variant: String = "") {
        // Cost estimate — pixel count × 4 bytes per pixel. NSImage size is in points,
        // but multiplied by representation scale where available.
        let pixels = image.representations.reduce(into: 0) { acc, rep in
            acc += rep.pixelsWide * rep.pixelsHigh
        }
        let cost = max(pixels * 4, Int(image.size.width * image.size.height * 4))
        cache.setObject(image, forKey: key(for: path, variant: variant), cost: cost)
    }
}
