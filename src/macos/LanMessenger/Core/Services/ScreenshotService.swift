import Foundation
import AppKit
import CoreGraphics
import CoreImage
import CoreMedia
import ImageIO
import ScreenCaptureKit

// Captures the user's main display via ScreenCaptureKit, writes a PNG to a temp
// location, and returns the path so the caller can route it through the
// existing FileTransferService.
//
// This service never touches the messaging or transfer pipelines directly — it
// only produces a file on disk.  The composer then calls AppModel.sendFile()
// with the returned path, exactly as if the user had dragged the screenshot in.
//
// Capture strategy (macOS 14+ vs macOS 13)
// -----------------------------------------
// macOS 14+ uses SCScreenshotManager.captureImage(), a purpose-built single-
// frame API introduced in macOS 14.  It has no stream lifecycle to manage,
// so there is nothing to start or stop — the system just returns one CGImage
// and is done.  This avoids the SCStream start/stop/teardown sequence that
// caused a main-thread freeze on macOS 14+ (see detailed note below).
//
// macOS 13.x falls back to an SCStream that is started, used for exactly one
// frame, and then stopped via the completion-handler form of stopCapture()
// (NOT the async/await form) to avoid any actor-hop.
//
// Why the SCStream approach caused a beachball on macOS 14+
// ----------------------------------------------------------
// The original code called stream.stopCapture() (async/await) from two
// concurrent places: once inside FrameCollector.resume() via Task.detached,
// and once in captureOneFrame() after firstImage() returns.  On macOS 14+,
// the Swift async wrapper for stopCapture() may hop to the main actor for
// its XPC teardown callbacks.  Having two concurrent calls serialised on the
// main thread — where each can take 2-4 s on macOS 15/26 while the capture
// session is torn down — blocked the main run loop long enough to show the
// spinning beachball and, on slower completion, required a force-quit.
//
// Threading
// ---------
//  • SCShareableContent.excludingDesktopWindows and SCScreenshotManager
//    are async APIs and must run off the main actor.  capturePrimaryDisplay()
//    is @MainActor for safe SwiftUI call-sites, but all heavy work runs in
//    a detached Task.
//  • PNG encoding uses CGImageDestination (Core Graphics), which is
//    explicitly documented as thread-safe, instead of NSBitmapImageRep.
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

            // Encode as PNG using CGImageDestination — the Core Graphics API
            // is explicitly thread-safe (unlike NSBitmapImageRep).
            let dir = try tempScreenshotDirectory()
            let filename = "Screenshot \(filenameTimestamp()).png"
            let url = dir.appendingPathComponent(filename)
            guard let dest = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil) else {
                throw ScreenshotError.writeFailed("CGImageDestinationCreateWithURL failed")
            }
            CGImageDestinationAddImage(dest, cgImage, nil)
            guard CGImageDestinationFinalize(dest) else {
                throw ScreenshotError.writeFailed("CGImageDestinationFinalize failed")
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

    /// Returns one CGImage from the primary display.
    /// On macOS 14+ uses SCScreenshotManager (single-frame, no stream lifecycle).
    /// On macOS 13.x falls back to SCStream with a one-frame collector.
    /// Declared `nonisolated` so it runs entirely off the main actor.
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
        // CGDisplayCreateImage used to return.  SCK widths/heights are in pixels.
        config.width = display.width
        config.height = display.height
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.queueDepth = 3
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)

        if #available(macOS 14.0, *) {
            // SCScreenshotManager.captureImage() is a purpose-built single-frame
            // capture API.  It has no stream to start or stop, so there is no
            // teardown race and no risk of the main actor being blocked by
            // concurrent stopCapture() calls.
            return try await SCScreenshotManager.captureImage(
                contentFilter: filter,
                configuration: config
            )
        }

        // ── macOS 13.x fallback: use SCStream for exactly one frame ──────────
        return try await captureOneFrameWithStream(filter: filter, config: config)
    }

    /// SCStream-based one-frame capture for macOS 13.x.
    /// On macOS 14+, captureOneFrame() uses SCScreenshotManager instead.
    nonisolated private static func captureOneFrameWithStream(
        filter: SCContentFilter,
        config: SCStreamConfiguration
    ) async throws -> CGImage {
        let collector = FrameCollector()
        let stream = SCStream(filter: filter, configuration: config, delegate: collector)
        let outputQueue = DispatchQueue(label: "com.dave.lanmessenger.screenshot",
                                        qos: .userInitiated)
        do {
            try stream.addStreamOutput(collector, type: .screen, sampleHandlerQueue: outputQueue)
        } catch {
            throw ScreenshotError.captureFailed("addStreamOutput failed: \(error.localizedDescription)")
        }

        try await stream.startCapture()

        // Wait for the first usable frame (or an error).  Time-bound so we
        // don't hang the UI forever if the GPU pipeline is wedged.
        let image = try await collector.firstImage(timeout: 5.0)

        // Stop the stream using the ObjC completion-handler form (not async/await)
        // so we never hop to the main actor.  Fire-and-forget: we already have
        // the image, so we don't need to await teardown completion.
        stream.stopCapture(completionHandler: nil)

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
/// any frames after that are silently ignored.  If a delegate error or a
/// timeout fires, the continuation is resumed with an error instead.
/// All mutable state is protected by a single lock so concurrent callbacks
/// cannot double-resume the continuation.
///
/// Note: this class is only used on macOS 13.x (the SCStream fallback path).
/// On macOS 14+ the SCScreenshotManager path is taken instead and this class
/// is never instantiated.
private final class FrameCollector: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {

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
        // Stream teardown is handled by captureOneFrameWithStream() after
        // firstImage() returns, using the ObjC completion-handler stopCapture()
        // to avoid any main-actor hop.  We intentionally do NOT call
        // stopCapture() here — that was the source of the concurrent double-stop
        // that caused the spinning beachball on macOS 14+.
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
