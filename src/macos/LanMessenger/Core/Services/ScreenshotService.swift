import Foundation
import AppKit
import CoreGraphics
import CoreImage
import CoreMedia
import ScreenCaptureKit

// Captures the user's main display via ScreenCaptureKit, writes a PNG to a temp
// location, and returns the path so the caller can route it through the
// existing FileTransferService.
//
// This service never touches the messaging or transfer pipelines directly — it
// only produces a file on disk.  The composer then calls AppModel.sendFile()
// with the returned path, exactly as if the user had dragged the screenshot in.
//
// Why ScreenCaptureKit
// --------------------
// The earlier implementation used `CGDisplayCreateImage`, which is deprecated
// in macOS 14.  ScreenCaptureKit is the supported replacement and works back
// to macOS 12.3, well below this project's 13.0 deployment target.  We use an
// `SCStream` with one frame and tear it down immediately so the API surface
// matches the one-shot semantics of the deprecated call.
//
// Threading
// ---------
//  • SCShareableContent.current and SCStream.startCapture are async APIs and
//    must run off the main actor.  The public capturePrimaryDisplay() is
//    declared on the main actor for safe call from SwiftUI, but its body
//    hops to a detached Task for all heavy work.
//  • The frame-delivery callback runs on a dedicated dispatch queue. We
//    convert the buffer once, hand the CGImage back via a continuation, and
//    immediately stop the stream so we never accumulate frames.
//
// Permissions
// -----------
//  • CGPreflightScreenCaptureAccess / CGRequestScreenCaptureAccess remain the
//    documented permission gate and are NOT deprecated.  We preflight first,
//    request once if needed, and surface a helpful error pointing at System
//    Settings if the user has previously denied.
//  • SCShareableContent.current will additionally throw if permission is
//    revoked between our gate and the capture; we translate that into the
//    same `permissionDenied` error so the UI message stays consistent.

enum ScreenshotError: LocalizedError {
    case permissionDenied
    case noDisplay
    case captureFailed(String)
    case writeFailed(String)

    var errorDescription: String? {
        switch self {
        case .permissionDenied:
            return "Screen recording permission is required. Open System Settings → Privacy & Security → Screen Recording and enable LAN Messenger, then try again."
        case .noDisplay:
            return "Could not find a display to capture."
        case .captureFailed(let detail):
            return "Could not capture the screen: \(detail)"
        case .writeFailed(let detail):
            return "Could not save the screenshot: \(detail)"
        }
    }
}

@MainActor
enum ScreenshotService {

    /// Returns true if the app currently has Screen Recording permission.
    static func hasPermission() -> Bool {
        if #available(macOS 11.0, *) {
            return CGPreflightScreenCaptureAccess()
        }
        // On macOS pre-11 the permission system does not exist — assume OK.
        return true
    }

    /// Asks the system for Screen Recording permission. Returns true if granted.
    /// The first call shows the system prompt; later calls return immediately.
    static func requestPermission() -> Bool {
        if #available(macOS 11.0, *) {
            return CGRequestScreenCaptureAccess()
        }
        return true
    }

    /// Captures the primary display and writes it as a PNG into a stable temp
    /// directory.  Returns the absolute file path on success.
    /// All ScreenCaptureKit work runs off the main actor.
    static func capturePrimaryDisplay() async throws -> String {
        let startedAt = Date()
        NetLogger.screenshot(event: "request", permission: hasPermission() ? "granted" : "unknown")

        // Permission gate.
        if !hasPermission() {
            let granted = requestPermission()
            if !granted {
                NetLogger.screenshot(event: "permission_denied", permission: "denied")
                throw ScreenshotError.permissionDenied
            }
        }

        // Off-main capture + write.
        return try await Task.detached(priority: .userInitiated) { () -> String in
            let cgImage: CGImage
            do {
                cgImage = try await Self.captureOneFrame()
            } catch let error as ScreenshotError {
                throw error
            } catch {
                // Common path: user revoked permission between our preflight
                // and the SCShareableContent call.  Map any SCK error that
                // mentions permission to permissionDenied; everything else
                // surfaces as captureFailed with the underlying message.
                let msg = (error as NSError).localizedDescription
                if msg.lowercased().contains("permission") || msg.lowercased().contains("not authorized") {
                    NetLogger.screenshot(event: "permission_denied", permission: "denied", reason: msg)
                    throw ScreenshotError.permissionDenied
                }
                NetLogger.screenshot(event: "failed", reason: msg)
                throw ScreenshotError.captureFailed(msg)
            }

            // Encode as PNG via NSBitmapImageRep.  Avoid NSImage so we don't
            // accumulate representation-scale ambiguities for callers that
            // later re-render the file.
            let rep = NSBitmapImageRep(cgImage: cgImage)
            rep.size = NSSize(width: cgImage.width, height: cgImage.height)
            guard let data = rep.representation(using: .png, properties: [:]) else {
                throw ScreenshotError.writeFailed("PNG encoding failed")
            }

            let dir = try tempScreenshotDirectory()
            let filename = "Screenshot \(filenameTimestamp()).png"
            let url = dir.appendingPathComponent(filename)
            do {
                try data.write(to: url, options: .atomic)
            } catch {
                throw ScreenshotError.writeFailed(error.localizedDescription)
            }

            let elapsedMs = Int(Date().timeIntervalSince(startedAt) * 1000)
            NetLogger.screenshot(
                event: "captured", display: "primary",
                widthPx: cgImage.width, heightPx: cgImage.height,
                permission: "granted", initMs: elapsedMs, path: url.path
            )
            return url.path
        }.value
    }

    /// Drives an SCStream just long enough to grab one frame.
    /// `nonisolated` because this is called from a detached Task and must not
    /// be funnelled back through the main actor.
    nonisolated private static func captureOneFrame() async throws -> CGImage {
        // Enumerate displays. `SCShareableContent.current` throws if Screen
        // Recording permission is missing — we map that case in the caller.
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let display = content.displays.first else {
            throw ScreenshotError.noDisplay
        }

        let filter = SCContentFilter(display: display, excludingWindows: [])

        let config = SCStreamConfiguration()
        // Capture at the display's pixel dimensions so the PNG matches what
        // CGDisplayCreateImage used to return.  SCK widths/heights are in
        // pixels.
        config.width = display.width
        config.height = display.height
        config.pixelFormat = kCVPixelFormatType_32BGRA
        // Color-accurate, no audio, and we don't care about the cursor for a
        // one-shot — leaving showsCursor at its default keeps behavioural
        // parity with the previous implementation.
        config.queueDepth = 3
        // Capture quickly — we only need one frame, no point waiting 1/60s.
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)

        let collector = FrameCollector()
        let stream = SCStream(filter: filter, configuration: config, delegate: collector)
        let outputQueue = DispatchQueue(label: "com.dave.lanmessenger.screenshot",
                                        qos: .userInitiated)
        do {
            try stream.addStreamOutput(collector, type: .screen, sampleHandlerQueue: outputQueue)
        } catch {
            throw ScreenshotError.captureFailed("addStreamOutput failed: \(error.localizedDescription)")
        }

        // Hand the stream to the collector so it can stop itself the moment a
        // frame arrives, instead of letting the stream keep delivering frames
        // we have no use for.
        collector.stream = stream

        try await stream.startCapture()

        // Wait for the first usable frame (or an error).  Time-bound so we
        // don't hang the UI forever if the GPU pipeline is wedged.
        let image = try await collector.firstImage(timeout: 5.0)

        // stopCapture is idempotent; collector may already have called it.
        do { try await stream.stopCapture() } catch { /* swallow — best effort */ }

        return image
    }

    // Marked `nonisolated` because they are called from a detached Task above
    // and never touch UI state.  The @MainActor on the enclosing enum applies
    // to the public API surface only.
    nonisolated private static func tempScreenshotDirectory() throws -> URL {
        let base = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("LanMessenger-Screenshots", isDirectory: true)
        if !FileManager.default.fileExists(atPath: base.path) {
            try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        }
        return base
    }

    nonisolated private static func filenameTimestamp() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        f.locale = Locale(identifier: "en_US_POSIX")
        return f.string(from: Date())
    }
}

// MARK: - Frame collector

/// Bridges SCStream's delegate-based callback model to async/await.
///
/// SCK delivers frames on a background queue.  The first valid sample buffer
/// is converted to a CGImage and handed back through the awaiting continuation;
/// any frames after that are ignored and the stream is stopped.  If a delegate
/// error or a timeout fires, the continuation is resumed with an error
/// instead.  All mutable state is protected by a single lock so concurrent
/// callbacks cannot double-resume the continuation.
private final class FrameCollector: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    weak var stream: SCStream?

    private let lock = NSLock()
    private var continuation: CheckedContinuation<CGImage, Error>?
    private let context = CIContext(options: nil)

    /// Suspends until a frame arrives, an error is raised, or `timeout` seconds elapse.
    func firstImage(timeout: TimeInterval) async throws -> CGImage {
        // Use a TaskGroup so the timeout race is structured and either path
        // cancels the other.
        return try await withThrowingTaskGroup(of: CGImage.self) { group in
            group.addTask {
                try await withCheckedThrowingContinuation { cont in
                    self.setContinuation(cont)
                }
            }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                throw ScreenshotError.captureFailed("timed out waiting for first frame")
            }
            // First result wins, cancel the loser.
            let result = try await group.next()!
            group.cancelAll()
            return result
        }
    }

    private func setContinuation(_ cont: CheckedContinuation<CGImage, Error>) {
        lock.lock()
        defer { lock.unlock() }
        self.continuation = cont
    }

    private func resume(with result: Result<CGImage, Error>) {
        lock.lock()
        guard let cont = self.continuation else { lock.unlock(); return }
        self.continuation = nil
        lock.unlock()
        switch result {
        case .success(let img): cont.resume(returning: img)
        case .failure(let err): cont.resume(throwing: err)
        }
        // Stop the stream now that we have what we needed.  Errors here are
        // not actionable; the next capture creates a fresh stream.
        if let stream = self.stream {
            Task.detached { try? await stream.stopCapture() }
        }
    }

    // MARK: SCStreamOutput

    func stream(_ stream: SCStream,
                didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
                of outputType: SCStreamOutputType) {
        guard outputType == .screen else { return }

        // SCK can emit "idle" frames whose attachments mark them as
        // non-displayable.  Skip those — only meaningful pixels count.
        // The attachment dictionary is keyed by `SCStreamFrameInfo`, and the
        // status value is the raw value of `SCFrameStatus`.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
           let first = attachments.first,
           let statusRaw = first[.status] as? Int,
           let status = SCFrameStatus(rawValue: statusRaw),
           status != .complete {
            return
        }

        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let ciImage = CIImage(cvPixelBuffer: pixelBuffer)
        guard let cg = context.createCGImage(ciImage, from: ciImage.extent) else {
            // Bad frame — don't surface an error yet; SCK will deliver more.
            return
        }
        resume(with: .success(cg))
    }

    // MARK: SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        NetLogger.screenshot(event: "interrupted", interruptionReason: error.localizedDescription)
        resume(with: .failure(ScreenshotError.captureFailed(error.localizedDescription)))
    }
}
