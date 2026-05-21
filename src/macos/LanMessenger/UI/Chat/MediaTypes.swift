import Foundation
import AppKit

// Extension-based media classification. Detection is intentionally client-side and
// extension-based — there is no protocol change. Incoming files arrive through the
// normal FileTransferService pipeline; the UI inspects the saved filename to decide
// whether to render an inline image, an inline video, or the generic file bubble.
enum MediaKind {
    case image
    case video
    case other

    static func from(path: String) -> MediaKind {
        let ext = (path as NSString).pathExtension.lowercased()
        if Self.imageExtensions.contains(ext) { return .image }
        if Self.videoExtensions.contains(ext) { return .video }
        return .other
    }

    static let imageExtensions: Set<String> = ["jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "bmp", "tiff"]
    static let videoExtensions: Set<String> = ["mp4", "mov", "m4v", "webm", "mkv", "avi"]
}

// Centralised, off-main-thread "reveal in Finder" with logging + user-visible errors.
// Returns a failure reason on the main actor or nil on success.
@MainActor
enum FinderReveal {
    /// Reveals a file in Finder.  Performs all blocking work off the main thread.
    /// `onError` is called on the main actor with a human-readable message when
    /// the file cannot be revealed (moved, deleted, or permission denied).
    static func reveal(path: String, onError: @escaping (String) -> Void) {
        let url = URL(fileURLWithPath: path)
        Task.detached(priority: .userInitiated) {
            let fm = FileManager.default
            guard fm.fileExists(atPath: path) else {
                NetLogger.warn("FinderReveal", "file no longer exists at \(path)")
                await MainActor.run { onError("File not found — it may have been moved or deleted.") }
                return
            }
            // `selectFile(_:inFileViewerRootedAtPath:)` returns false if Finder
            // refuses the request (permission issues, sandbox, missing file).
            let ok = NSWorkspace.shared.selectFile(url.path, inFileViewerRootedAtPath: "")
            if !ok {
                // Fall back to the higher-level API; it sometimes succeeds when the
                // older selectFile call refuses (e.g. on quarantined volumes).
                NSWorkspace.shared.activateFileViewerSelecting([url])
                NetLogger.info("FinderReveal", "fallback activateFileViewerSelecting for \(path)")
            } else {
                NetLogger.verbose("FinderReveal", "revealed \(path)")
            }
        }
    }
}
