import XCTest
import AppKit
@testable import LanMessenger

// Cover for pasting attachments into the composer and for the on-disk naming
// of generated attachments.
//
// The interesting logic is `decide`: ⌘V has to keep working as a plain text
// paste from every app that puts an image representation on the pasteboard
// alongside its text (browsers, Word, Notes). Getting that precedence wrong
// doesn't fail loudly — it silently turns every text paste into a file send.
final class AttachmentPasteboardTests: XCTestCase {

    // MARK: - Paste precedence

    func testFilesWinOverEverything() {
        XCTAssertEqual(AttachmentPasteboard.decide(hasFileURLs: true, hasImage: true, hasText: true),
                       .attachFiles)
        XCTAssertEqual(AttachmentPasteboard.decide(hasFileURLs: true, hasImage: false, hasText: false),
                       .attachFiles)
    }

    // Copying in Finder, Preview's "Copy" on an image, a screenshot to the
    // clipboard: bitmap and nothing else.
    func testBitmapAloneIsAttached() {
        XCTAssertEqual(AttachmentPasteboard.decide(hasFileURLs: false, hasImage: true, hasText: false),
                       .attachImage)
    }

    // The regression this rule exists for. A browser or word-processor copy
    // carries text AND an image flavour; the user meant the text.
    func testBitmapAlongsideTextStaysATextPaste() {
        XCTAssertEqual(AttachmentPasteboard.decide(hasFileURLs: false, hasImage: true, hasText: true),
                       .insertText)
    }

    func testPlainTextIsATextPaste() {
        XCTAssertEqual(AttachmentPasteboard.decide(hasFileURLs: false, hasImage: false, hasText: true),
                       .insertText)
    }

    // Nothing usable on the pasteboard — fall through to AppKit rather than
    // swallowing the keystroke.
    func testEmptyPasteboardIsATextPaste() {
        XCTAssertEqual(AttachmentPasteboard.decide(hasFileURLs: false, hasImage: false, hasText: false),
                       .insertText)
    }

    // MARK: - Bitmap flavour selection

    func testPngIsPreferredOverTiff() {
        let type = AttachmentPasteboard.imageType(in: [.tiff, .png, .string])
        XCTAssertEqual(type, .png)
    }

    func testJpegIsPreferredOverTiff() {
        let jpeg = NSPasteboard.PasteboardType("public.jpeg")
        XCTAssertEqual(AttachmentPasteboard.imageType(in: [.tiff, jpeg]), jpeg)
        XCTAssertEqual(AttachmentPasteboard.fileExtension(for: jpeg), "jpg")
    }

    // TIFF is AppKit's synthesised catch-all flavour; we re-encode it as PNG,
    // so the extension must say png or the receiving client classifies the
    // attachment by a lie.
    func testTiffIsSavedAsPng() {
        XCTAssertEqual(AttachmentPasteboard.imageType(in: [.tiff]), .tiff)
        XCTAssertEqual(AttachmentPasteboard.fileExtension(for: .tiff), "png")
    }

    func testNoImageFlavourReturnsNil() {
        XCTAssertNil(AttachmentPasteboard.imageType(in: [.string, .html]))
    }

    // MARK: - Provider items

    // loadItem for public.file-url hands back Data from most sources and a real
    // URL from some. Accepting only one of the two silently drops attachments
    // depending on which app the drag came from.
    func testFileURLIsReadFromDataRepresentation() {
        let url = URL(fileURLWithPath: "/tmp/lan-messenger-test/report.pdf")
        let asData = url.dataRepresentation as NSData
        XCTAssertEqual(AttachmentPasteboard.fileURL(fromProviderItem: asData)?.path, url.path)
    }

    func testFileURLIsReadFromURLItem() {
        let url = URL(fileURLWithPath: "/tmp/lan-messenger-test/report.pdf")
        XCTAssertEqual(AttachmentPasteboard.fileURL(fromProviderItem: url as NSURL)?.path, url.path)
    }

    func testUnusableProviderItemIsIgnored() {
        XCTAssertNil(AttachmentPasteboard.fileURL(fromProviderItem: nil))
        XCTAssertNil(AttachmentPasteboard.fileURL(fromProviderItem: NSNumber(value: 7)))
    }

    // MARK: - Drag out

    func testOutgoingProviderIsNilForMissingFile() {
        XCTAssertNil(AttachmentPasteboard.outgoingProvider(forFileAt: "/tmp/definitely-not-here-\(UUID().uuidString)"))
    }

    func testOutgoingProviderCarriesTheFilename() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("lm-drag-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let file = dir.appendingPathComponent("quarterly notes.txt")
        try Data("hello".utf8).write(to: file)

        let provider = try XCTUnwrap(AttachmentPasteboard.outgoingProvider(forFileAt: file.path))
        XCTAssertEqual(provider.suggestedName, "quarterly notes.txt")
    }
}

// Naming and placement of attachments the app generates itself.
final class AttachmentStoreTests: XCTestCase {

    // The timestamp goes into a filename, so it must not contain ":" or "/".
    func testTimestampComponentIsFilenameSafe() {
        let stamp = AttachmentStore.timestampComponent(Date(timeIntervalSince1970: 1_757_600_709))
        XCTAssertFalse(stamp.contains(":"))
        XCTAssertFalse(stamp.contains("/"))
        XCTAssertTrue(stamp.contains(" at "), "unexpected format: \(stamp)")
    }

    func testUniqueURLUsesPlainNameWhenFree() {
        let dir = URL(fileURLWithPath: "/tmp/shots")
        let url = AttachmentStore.uniqueURL(in: dir, basename: "Pasted image", ext: "png",
                                            fileExists: { _ in false })
        XCTAssertEqual(url.lastPathComponent, "Pasted image.png")
    }

    // Two pastes inside the same second produce the same timestamp. Without
    // dedup the second would overwrite the first — retroactively changing what
    // an already-sent bubble points at.
    func testUniqueURLAvoidsCollisions() {
        let dir = URL(fileURLWithPath: "/tmp/shots")
        let taken: Set<String> = [
            "/tmp/shots/Pasted image.png",
            "/tmp/shots/Pasted image-1.png",
        ]
        let url = AttachmentStore.uniqueURL(in: dir, basename: "Pasted image", ext: "png",
                                            fileExists: { taken.contains($0) })
        XCTAssertEqual(url.lastPathComponent, "Pasted image-2.png")
    }

    // Exhausting the numbered suffixes must still yield a usable path rather
    // than looping or overwriting.
    func testUniqueURLFallsBackWhenAllSuffixesTaken() {
        let dir = URL(fileURLWithPath: "/tmp/shots")
        let url = AttachmentStore.uniqueURL(in: dir, basename: "Pasted image", ext: "png",
                                            fileExists: { _ in true })
        XCTAssertTrue(url.lastPathComponent.hasPrefix("Pasted image-"))
        XCTAssertEqual(url.pathExtension, "png")
        XCTAssertFalse(url.lastPathComponent.contains("-999."))
    }

    // Attachments must not land in NSTemporaryDirectory(): history stores
    // absolute paths and macOS sweeps temp between reboots, which is what
    // turned older screenshots into "File no longer available".
    func testDirectoryIsNotTheSystemTempDirectory() throws {
        let dir = try AttachmentStore.directory(customPath: NSTemporaryDirectory() + "lm-store-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        XCTAssertTrue(FileManager.default.fileExists(atPath: dir.path))

        let defaultDir = try AttachmentStore.directory()
        XCTAssertFalse(defaultDir.path.hasPrefix(NSTemporaryDirectory()),
                       "default attachment directory must survive a reboot")
    }
}
