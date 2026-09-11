import Foundation
import AppKit
import UniformTypeIdentifiers

/// What a paste into the composer should do.
enum PasteAction: Equatable {
    /// The pasteboard carries file URLs — send them as attachments.
    case attachFiles
    /// The pasteboard carries bitmap data with no text — write it to disk and
    /// send it as an image attachment.
    case attachImage
    /// Ordinary text paste; hand back to the text view.
    case insertText
}

/// Reads attachments off an `NSPasteboard` (⌘V in the composer) and off the
/// item providers a drag-and-drop supplies.
///
/// The decision of *what* a paste means is split out as a pure function so the
/// precedence rules are testable without a pasteboard server.
enum AttachmentPasteboard {

    // MARK: - Decision

    /// Precedence: real files beat a bitmap, and a bitmap only wins when there
    /// is no text to paste instead.
    ///
    /// That last rule matters more than it looks. Copying from a browser, Word,
    /// or Preview's annotation tools puts an image representation on the
    /// pasteboard *alongside* the text — treating those as image attachments
    /// would make ⌘V stop pasting text at all from half the apps on the system.
    static func decide(hasFileURLs: Bool, hasImage: Bool, hasText: Bool) -> PasteAction {
        if hasFileURLs { return .attachFiles }
        if hasImage && !hasText { return .attachImage }
        return .insertText
    }

    /// Bitmap flavours we can write straight to disk, best first. PNG and JPEG
    /// are already file formats; TIFF is the flavour AppKit synthesises for
    /// almost any image on the pasteboard and gets transcoded to PNG.
    static func imageType(in types: [NSPasteboard.PasteboardType]) -> NSPasteboard.PasteboardType? {
        let preferred: [NSPasteboard.PasteboardType] = [
            .png,
            NSPasteboard.PasteboardType("public.jpeg"),
            .tiff,
        ]
        return preferred.first { types.contains($0) }
    }

    /// File extension used for a pasted bitmap of the given flavour. TIFF is
    /// re-encoded as PNG rather than written through — screenshots and copied
    /// images are overwhelmingly screen content, where PNG is both smaller and
    /// what the receiving side expects to preview inline.
    static func fileExtension(for type: NSPasteboard.PasteboardType) -> String {
        type == NSPasteboard.PasteboardType("public.jpeg") ? "jpg" : "png"
    }

    static func action(for pasteboard: NSPasteboard) -> PasteAction {
        let types = pasteboard.types ?? []
        return decide(
            hasFileURLs: !fileURLs(on: pasteboard).isEmpty,
            hasImage: imageType(in: types) != nil,
            hasText: (pasteboard.string(forType: .string)?.isEmpty == false)
        )
    }

    // MARK: - Reading

    /// File URLs on the pasteboard, in the order the source app put them there.
    static func fileURLs(on pasteboard: NSPasteboard) -> [URL] {
        let options: [NSPasteboard.ReadingOptionKey: Any] = [.urlReadingFileURLsOnly: true]
        let objects = pasteboard.readObjects(forClasses: [NSURL.self], options: options) as? [URL]
        return objects ?? []
    }

    /// Copies the pasteboard's bitmap bytes out. NSPasteboard is not safe to
    /// touch off the main thread, so callers take the payload here and hand it
    /// to `writeImage(data:type:...)` on a background queue — copying bytes is
    /// cheap, re-encoding a 4K bitmap is not.
    static func imagePayload(on pasteboard: NSPasteboard) -> (data: Data, type: NSPasteboard.PasteboardType)? {
        guard let type = imageType(in: pasteboard.types ?? []),
              let raw = pasteboard.data(forType: type) else { return nil }
        return (raw, type)
    }

    /// Writes the pasteboard's bitmap to the attachment directory and returns
    /// the path, or nil when there is no bitmap / the write fails.
    static func writeImage(from pasteboard: NSPasteboard,
                           customDirectory: String = ConfigStore.shared.config.screenshotDir,
                           now: Date = Date()) -> String? {
        guard let payload = imagePayload(on: pasteboard) else { return nil }
        return writeImage(data: payload.data, type: payload.type,
                          customDirectory: customDirectory, now: now)
    }

    /// Encodes and writes bitmap bytes already taken off the pasteboard.
    /// Touches no AppKit pasteboard state, so it is safe off the main thread.
    static func writeImage(data raw: Data,
                           type: NSPasteboard.PasteboardType,
                           customDirectory: String = ConfigStore.shared.config.screenshotDir,
                           now: Date = Date()) -> String? {
        let ext = fileExtension(for: type)
        let payload: Data?
        if type == .tiff {
            payload = NSBitmapImageRep(data: raw)?.representation(using: .png, properties: [:])
        } else {
            payload = raw
        }
        guard let data = payload else {
            NetLogger.warn("Paste", "could not transcode pasted \(type.rawValue) to \(ext)")
            return nil
        }

        do {
            let dir = try AttachmentStore.directory(customPath: customDirectory)
            let url = AttachmentStore.uniqueURL(
                in: dir,
                basename: "Pasted image \(AttachmentStore.timestampComponent(now))",
                ext: ext
            )
            try data.write(to: url, options: .atomic)
            NetLogger.ui(event: "attachment_pasted", detail: url.lastPathComponent)
            return url.path
        } catch {
            NetLogger.warn("Paste", "writing pasted image failed: \(error.localizedDescription)")
            return nil
        }
    }

    /// Full resolve for a ⌘V in the composer: the attachment paths to send, or
    /// an empty array when the paste is ordinary text.
    static func attachmentPaths(from pasteboard: NSPasteboard,
                                customDirectory: String = ConfigStore.shared.config.screenshotDir,
                                now: Date = Date()) -> [String] {
        switch action(for: pasteboard) {
        case .attachFiles:
            return fileURLs(on: pasteboard).map(\.path)
        case .attachImage:
            return writeImage(from: pasteboard, customDirectory: customDirectory, now: now).map { [$0] } ?? []
        case .insertText:
            return []
        }
    }

    // MARK: - Drag and drop

    /// The pasteboard/UTI types a chat thread accepts as a drop.
    static let dropTypes: [UTType] = [.fileURL]

    /// Resolves dropped item providers to local file paths. Providers hand
    /// their data back asynchronously and out of order, so the results are
    /// re-sorted into the order the user dropped them before `completion` runs
    /// once on the main queue.
    static func loadDroppedPaths(from providers: [NSItemProvider],
                                 completion: @escaping ([String]) -> Void) {
        let group = DispatchGroup()
        let lock = NSLock()
        var found: [(Int, String)] = []

        for (index, provider) in providers.enumerated() {
            guard provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) else { continue }
            group.enter()
            provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                defer { group.leave() }
                guard let url = fileURL(fromProviderItem: item) else { return }
                lock.lock()
                found.append((index, url.path))
                lock.unlock()
            }
        }

        group.notify(queue: .main) {
            completion(found.sorted { $0.0 < $1.0 }.map(\.1))
        }
    }

    /// `loadItem` for `public.file-url` hands back a `Data` holding the URL's
    /// bytes from most sources but a real `URL` from some — accept both rather
    /// than silently dropping the attachment.
    static func fileURL(fromProviderItem item: NSSecureCoding?) -> URL? {
        if let url = item as? URL { return url }
        if let data = item as? Data { return URL(dataRepresentation: data, relativeTo: nil) }
        return nil
    }

    /// Item provider used when the user drags an attachment *out* of a bubble
    /// into Finder, Mail, or another app. Returns nil when the file is gone, so
    /// callers can skip wiring up a drag that would produce nothing.
    static func outgoingProvider(forFileAt path: String) -> NSItemProvider? {
        let url = URL(fileURLWithPath: path)
        guard FileManager.default.fileExists(atPath: path) else { return nil }
        let provider = NSItemProvider(contentsOf: url)
        provider?.suggestedName = url.lastPathComponent
        return provider
    }
}
